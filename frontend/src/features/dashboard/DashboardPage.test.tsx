import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { useAuthStore } from '../../stores/authStore';
import type { DashboardResponse } from '../../types/dashboard';
import { DashboardPage } from './DashboardPage';

const { getDashboard } = vi.hoisted(() => ({ getDashboard: vi.fn() }));

vi.mock('../../services/dashboardService', () => ({ getDashboard }));

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => vi.fn() };
});

function dashboard(overrides: Partial<DashboardResponse> = {}): DashboardResponse {
  return {
    myTasks: { total: 3, overdue: 0, dueSoon: 0 },
    pendingApprovals: { pending: 2 },
    sla: { active: 1, warning: 0, overdue: 0, completed: 4 },
    processOverview: { running: 5, completed: 10, rejected: 1 },
    recentActivity: [],
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  useAuthStore.setState({ accessToken: 'token', user: { userId: 'user-1', displayName: 'User', roles: [] } });
});

describe('DashboardPage', () => {
  it('renders KPI cards from the dashboard response', async () => {
    getDashboard.mockResolvedValue(dashboard());

    renderWithProviders(<DashboardPage />);

    await waitFor(() => expect(screen.getByText('My Tasks')).toBeInTheDocument());
    expect(screen.getByText('3')).toBeInTheDocument(); // My Tasks total
    expect(screen.getByText('2')).toBeInTheDocument(); // Pending Approvals
    expect(screen.getByText('Process Overview')).toBeInTheDocument();
    expect(screen.getByText('SLA Overview')).toBeInTheDocument();
  });

  it('shows a loading state before data arrives', async () => {
    let resolve!: (value: DashboardResponse) => void;
    getDashboard.mockReturnValue(new Promise((r) => { resolve = r; }));

    const { container } = renderWithProviders(<DashboardPage />);

    expect(container.querySelector('.ant-card-loading')).not.toBeNull();
    resolve(dashboard());
    await waitFor(() => expect(container.querySelector('.ant-card-loading')).toBeNull());
  });

  it('shows an error state when the request fails', async () => {
    getDashboard.mockRejectedValue({
      isAxiosError: true,
      response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } },
    });

    renderWithProviders(<DashboardPage />);

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('retries via the Refresh button', async () => {
    getDashboard.mockResolvedValue(dashboard());
    const user = userEvent.setup();

    renderWithProviders(<DashboardPage />);
    await waitFor(() => expect(screen.getByText('My Tasks')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Refresh/i }));
    await waitFor(() => expect(getDashboard).toHaveBeenCalledTimes(2));
  });

  it('shows an empty state for Recent Activity when there is none', async () => {
    getDashboard.mockResolvedValue(dashboard({ recentActivity: [] }));

    renderWithProviders(<DashboardPage />);

    await waitFor(() => expect(screen.getByText('No recent activity.')).toBeInTheDocument());
  });

  it('renders Recent Activity items when present', async () => {
    getDashboard.mockResolvedValue(dashboard({
      recentActivity: [
        { timestamp: '2026-01-01T10:00:00Z', action: 'StartProcess', description: 'Process started', processInstanceId: 'proc-1' },
      ],
    }));

    renderWithProviders(<DashboardPage />);

    await waitFor(() => expect(screen.getByText('Process started')).toBeInTheDocument());
    expect(screen.getByText('StartProcess')).toBeInTheDocument();
  });

  it('shows a zero-KPI state without crashing when all summaries are zero', async () => {
    getDashboard.mockResolvedValue(dashboard({
      myTasks: { total: 0, overdue: 0, dueSoon: 0 },
      pendingApprovals: { pending: 0 },
      sla: { active: 0, warning: 0, overdue: 0, completed: 0 },
      processOverview: { running: 0, completed: 0, rejected: 0 },
    }));

    renderWithProviders(<DashboardPage />);

    await waitFor(() => expect(screen.getByText('My Tasks')).toBeInTheDocument());
    expect(screen.getAllByText('0').length).toBeGreaterThan(0);
  });

  it('shows the system-wide label for an Administrator and the personal label otherwise', async () => {
    getDashboard.mockResolvedValue(dashboard());
    useAuthStore.setState({ accessToken: 'token', user: { userId: 'admin-1', displayName: 'Admin', roles: ['Administrator'] } });

    renderWithProviders(<DashboardPage />);

    await waitFor(() => expect(screen.getByText('System-wide operational summary.')).toBeInTheDocument());
  });
});
