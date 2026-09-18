import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { ReportSummary } from '../../types/report';
import { ReportingPage } from './ReportingPage';

const { getReportSummary, listReportDetails, exportReportCsv } = vi.hoisted(() => ({
  getReportSummary: vi.fn(),
  listReportDetails: vi.fn(),
  exportReportCsv: vi.fn(),
}));

vi.mock('../../services/reportService', () => ({ getReportSummary, listReportDetails, exportReportCsv }));
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

function summary(overrides: Partial<ReportSummary> = {}): ReportSummary {
  return {
    process: { total: 10, running: 4, completed: 5, rejected: 1 },
    processBreakdown: [
      { processDefinitionId: 'def-1', processDefinitionKey: 'purchase-request', processDefinitionName: 'Purchase Request', total: 10, running: 4, completed: 5, rejected: 1 },
    ],
    taskSummary: { total: 20, pending: 5, inProgress: 2, completed: 12, rejected: 1, returned: 0, cancelled: 0, expired: 0 },
    approvalSummary: { total: 8, pending: 2, approved: 5, rejected: 1, returned: 0, cancelled: 0 },
    slaSummary: { active: 3, warning: 1, overdue: 1, completed: 5, completedWithinSla: 4, completedBreachedSla: 1, complianceRate: 0.8 },
    ...overrides,
  };
}

function detailItem() {
  return {
    processInstanceId: 'proc-1',
    processDefinitionId: 'def-1',
    processDefinitionKey: 'purchase-request',
    processDefinitionName: 'Purchase Request',
    status: 'Running' as const,
    initiatorId: 'user-1',
    initiatorDisplayName: 'Amy',
    startedAt: '2026-09-16T09:20:00Z',
    updatedAt: '2026-09-16T10:15:00Z',
    currentTaskId: 'task-1',
    currentTaskName: 'Manager Approval',
    currentTaskStatus: 'Pending' as const,
    currentTaskIsApprovalTask: true,
    currentTaskAssigneeDisplay: 'Pending approval (1 candidate)',
    activeTaskCount: 1,
    slaStatus: 'Warning' as const,
    slaDueAt: '2026-09-17T09:20:00Z',
  };
}

function pagedDetails(items = [detailItem()], totalCount = items.length) {
  return { items, totalCount, page: 1, pageSize: 20 };
}

beforeEach(() => {
  vi.clearAllMocks();
  getReportSummary.mockResolvedValue(summary());
  listReportDetails.mockResolvedValue(pagedDetails());
});

describe('ReportingPage', () => {
  it('renders summary cards from the backend response', async () => {
    renderWithProviders(<ReportingPage />);

    await waitFor(() => expect(screen.getByText('Total Processes')).toBeInTheDocument());
    expect(screen.getAllByText('10').length).toBeGreaterThan(0); // Total Processes value
    expect(screen.getByText('Total Tasks')).toBeInTheDocument();
    expect(screen.getByText('Total Approvals')).toBeInTheDocument();
    expect(screen.getByText('SLA Compliance')).toBeInTheDocument();
    expect(screen.getByText('80.0%')).toBeInTheDocument();
  });

  it('renders the process breakdown table', async () => {
    renderWithProviders(<ReportingPage />);

    await waitFor(() => expect(screen.getByText('Process Breakdown')).toBeInTheDocument());
    await waitFor(() => expect(screen.getAllByText('Purchase Request').length).toBeGreaterThan(0));
  });

  it('renders the detail table rows', async () => {
    renderWithProviders(<ReportingPage />);

    await waitFor(() => expect(screen.getByText('Report Detail')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText('Manager Approval')).toBeInTheDocument());
  });

  it('shows an empty state when no detail rows match', async () => {
    listReportDetails.mockResolvedValue(pagedDetails([], 0));

    renderWithProviders(<ReportingPage />);

    await waitFor(() => expect(screen.getByText('No process instances match your filters.')).toBeInTheDocument());
  });

  it('shows an error state when the summary request fails', async () => {
    getReportSummary.mockRejectedValue({
      isAxiosError: true,
      response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } },
    });

    renderWithProviders(<ReportingPage />);

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('shows a 403 error state for an unauthorized detail request', async () => {
    listReportDetails.mockRejectedValue({
      isAxiosError: true,
      response: { status: 403, data: { code: 'FORBIDDEN', message: "You don't have permission to perform this action.", traceId: 't-1' } },
    });

    renderWithProviders(<ReportingPage />);

    await waitFor(() => expect(screen.getByText("You don't have permission to perform this action.")).toBeInTheDocument());
  });

  it('triggers CSV export when the Export button is clicked', async () => {
    exportReportCsv.mockResolvedValue(new Blob(['a,b\n1,2'], { type: 'text/csv' }));
    // jsdom has no real object URL support by default in this environment.
    URL.createObjectURL = vi.fn().mockReturnValue('blob:mock');
    URL.revokeObjectURL = vi.fn();
    const user = userEvent.setup();

    renderWithProviders(<ReportingPage />);
    await waitFor(() => expect(screen.getByText('Total Processes')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Export CSV/i }));
    await waitFor(() => expect(exportReportCsv).toHaveBeenCalledTimes(1));
  });

  it('resets filters via the Reset button', async () => {
    const user = userEvent.setup();

    renderWithProviders(<ReportingPage />);
    await waitFor(() => expect(screen.getByText('Total Processes')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: 'Reset' }));
    await waitFor(() => expect(getReportSummary).toHaveBeenCalledWith(expect.objectContaining({ processDefinitionId: undefined, status: undefined })));
  });
});
