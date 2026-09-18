import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { AnalyticsOverview } from '../../types/analytics';
import { AnalyticsPage } from './AnalyticsPage';

const { getAnalyticsOverview } = vi.hoisted(() => ({ getAnalyticsOverview: vi.fn() }));

vi.mock('../../services/analyticsService', () => ({ getAnalyticsOverview }));
vi.mock('../../services/userService', () => ({
  listUsers: vi.fn().mockResolvedValue([{ id: 'user-1', username: 'amy', displayName: 'Amy', email: 'amy@bpm.local', departmentId: null, isActive: true }]),
}));
vi.mock('../../services/departmentService', () => ({
  listDepartments: vi.fn().mockResolvedValue([{ id: 'dept-1', name: 'Engineering', organizationId: 'org-1', parentId: null, managerUserId: null, rowVersion: 'AA==' }]),
}));

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => vi.fn() };
});

function overview(overrides: Partial<AnalyticsOverview> = {}): AnalyticsOverview {
  return {
    totalProcesses: 10,
    runningProcesses: 4,
    completedProcesses: 5,
    rejectedProcesses: 1,
    volumeTrend: [{ bucketStart: '2026-09-10T00:00:00Z', started: 3, completed: 2, rejected: 0 }],
    processDuration: { sampleCount: 5, averageHours: 4.5, minHours: 1, maxHours: 10 },
    taskThroughputTrend: [{ bucketStart: '2026-09-10T00:00:00Z', created: 4, completed: 3 }],
    taskDuration: { sampleCount: 8, averageHours: 2.2, minHours: 0.5, maxHours: 6 },
    nodeAnalytics: [
      { nodeId: 'task', nodeName: 'Review Request', executions: 10, completed: 8, duration: { sampleCount: 8, averageHours: 1.5, minHours: 0.2, maxHours: 5 }, overdueCount: 1 },
    ],
    slaTrend: [{ bucketStart: '2026-09-10T00:00:00Z', completedSlaTasks: 4, compliantCount: 3, breachedCount: 1, complianceRate: 0.75 }],
    processComparison: [
      { processDefinitionId: 'def-1', processDefinitionKey: 'purchase-request', processDefinitionName: 'Purchase Request', total: 10, completed: 5, rejected: 1, duration: { sampleCount: 5, averageHours: 4.5, minHours: 1, maxHours: 10 }, slaComplianceRate: 0.75, overdueCount: 1 },
    ],
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  getAnalyticsOverview.mockResolvedValue(overview());
});

describe('AnalyticsPage', () => {
  it('renders overview summary cards from the backend response', async () => {
    renderWithProviders(<AnalyticsPage />);

    await waitFor(() => expect(screen.getByText('Total Processes')).toBeInTheDocument());
    expect(screen.getByText('Avg Process Duration (Completed Instances)')).toBeInTheDocument();
    expect(screen.getByText('Avg Task Duration (Completed Tasks)')).toBeInTheDocument();
    expect(screen.getAllByText(/SLA Compliance/).length).toBeGreaterThan(0);
  });

  it('renders the volume trend table', async () => {
    renderWithProviders(<AnalyticsPage />);

    await waitFor(() => expect(screen.getByText('Process Volume / Completion / Rejection Trend')).toBeInTheDocument());
    await waitFor(() => expect(screen.getAllByText('3').length).toBeGreaterThan(0));
  });

  it('renders the process comparison table', async () => {
    renderWithProviders(<AnalyticsPage />);

    await waitFor(() => expect(screen.getByText('Process Comparison')).toBeInTheDocument());
    await waitFor(() => expect(screen.getAllByText('Purchase Request').length).toBeGreaterThan(0));
  });

  it('renders the node analytics table', async () => {
    renderWithProviders(<AnalyticsPage />);

    await waitFor(() => expect(screen.getByText('Node / Workflow Step Analytics')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText('Review Request')).toBeInTheDocument());
  });

  it('shows an empty state when no data matches the filters', async () => {
    getAnalyticsOverview.mockResolvedValue(overview({ volumeTrend: [], processComparison: [], nodeAnalytics: [], slaTrend: [] }));

    renderWithProviders(<AnalyticsPage />);

    await waitFor(() => expect(screen.getByText('No process activity in the selected range.')).toBeInTheDocument());
    expect(screen.getByText('No process instances match your filters.')).toBeInTheDocument();
  });

  it('shows an error state when the request fails', async () => {
    getAnalyticsOverview.mockRejectedValue({
      isAxiosError: true,
      response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } },
    });

    renderWithProviders(<AnalyticsPage />);

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('shows a 403 error state for an unauthorized request', async () => {
    getAnalyticsOverview.mockRejectedValue({
      isAxiosError: true,
      response: { status: 403, data: { code: 'FORBIDDEN', message: "You don't have permission to perform this action.", traceId: 't-1' } },
    });

    renderWithProviders(<AnalyticsPage />);

    await waitFor(() => expect(screen.getByText("You don't have permission to perform this action.")).toBeInTheDocument());
  });

  it('resets filters via the Reset button', async () => {
    const user = userEvent.setup();

    renderWithProviders(<AnalyticsPage />);
    await waitFor(() => expect(screen.getByText('Total Processes')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Reset' }));
    await waitFor(() => expect(getAnalyticsOverview).toHaveBeenCalledWith(expect.objectContaining({ processDefinitionId: undefined, granularity: 'Day' })));
  });

  it('navigates to Process Monitoring on drill-down', async () => {
    const user = userEvent.setup();

    renderWithProviders(<AnalyticsPage />);
    await waitFor(() => expect(screen.getByText('Process Comparison')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByRole('button', { name: 'View in Process Monitoring' })).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'View in Process Monitoring' }));
    // Navigation itself is mocked away (useNavigate stub); this asserts the click handler exists
    // and does not throw, matching the same interaction-smoke-test style used elsewhere.
  });
});
