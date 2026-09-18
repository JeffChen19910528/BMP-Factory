import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { ApprovalWorklistItem } from '../../types/task';
import { ApprovalsPage } from './ApprovalsPage';

const { listApprovalWorklist } = vi.hoisted(() => ({ listApprovalWorklist: vi.fn() }));

vi.mock('../../services/taskService', () => ({ listApprovalWorklist, listMyTasks: vi.fn() }));
vi.mock('../../services/userService', () => ({
  listUsers: vi.fn().mockResolvedValue([{ id: 'applicant-1', username: 'john', displayName: 'John Smith', email: 'john@bpm.local', departmentId: null, isActive: true }]),
}));

const navigateMock = vi.fn();
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => navigateMock };
});

function item(overrides: Partial<ApprovalWorklistItem> = {}): ApprovalWorklistItem {
  return {
    taskId: 'task-1',
    processInstanceId: 'proc-instance-1',
    processDefinitionKey: 'expense-request',
    processDefinitionName: 'Expense Request',
    taskName: 'Manager Approval',
    applicantId: 'applicant-1',
    assigneeId: 'manager-1',
    assigneeRole: null,
    taskStatus: 'Pending',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-02T00:00:00Z',
    dueAt: null,
    approval: { policy: 'AnyOne', status: 'Pending', requiredCount: 1, approvedCount: 0, rejectedCount: 0, assignments: [] },
    ...overrides,
  };
}

function paged(items: ApprovalWorklistItem[], totalCount = items.length, page = 1, pageSize = 20) {
  return { items, totalCount, page, pageSize };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('ApprovalsPage', () => {
  it('shows the list of approval items with process/task/applicant/status/progress', async () => {
    listApprovalWorklist.mockResolvedValue(paged([item()]));

    renderWithProviders(<ApprovalsPage />, { route: '/approvals', path: '/approvals' });

    await waitFor(() => expect(screen.getByText('Expense Request')).toBeInTheDocument());
    expect(screen.getByText('Manager Approval')).toBeInTheDocument();
    expect(screen.getByText('John Smith')).toBeInTheDocument();
    expect(screen.getByText('AnyOne 0/1')).toBeInTheDocument();
  });

  it('defaults to the Pending tab and shows a Pending-specific empty state', async () => {
    listApprovalWorklist.mockResolvedValue(paged([]));

    renderWithProviders(<ApprovalsPage />, { route: '/approvals', path: '/approvals' });

    await waitFor(() => expect(screen.getByText('No pending approvals.')).toBeInTheDocument());
    expect(listApprovalWorklist).toHaveBeenCalledWith(expect.objectContaining({ status: 'Pending' }));
  });

  it('shows an error state when the request fails', async () => {
    listApprovalWorklist.mockRejectedValue({
      isAxiosError: true,
      response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } },
    });

    renderWithProviders(<ApprovalsPage />, { route: '/approvals', path: '/approvals' });

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('switching to the Returned tab re-queries with status=Returned and its own empty text', async () => {
    listApprovalWorklist.mockResolvedValue(paged([]));
    const user = userEvent.setup();

    renderWithProviders(<ApprovalsPage />, { route: '/approvals', path: '/approvals' });
    await waitFor(() => expect(listApprovalWorklist).toHaveBeenCalled());

    await user.click(screen.getByText('Returned'));

    await waitFor(() => expect(listApprovalWorklist).toHaveBeenCalledWith(expect.objectContaining({ status: 'Returned' })));
    expect(await screen.findByText('No returned approvals.')).toBeInTheDocument();
  });

  it('the All tab omits the status filter entirely', async () => {
    listApprovalWorklist.mockResolvedValue(paged([]));
    const user = userEvent.setup();

    renderWithProviders(<ApprovalsPage />, { route: '/approvals', path: '/approvals' });
    await waitFor(() => expect(listApprovalWorklist).toHaveBeenCalled());

    await user.click(screen.getByText('All'));

    await waitFor(() => {
      const lastCall = listApprovalWorklist.mock.calls.at(-1)![0];
      expect(lastCall.status).toBeUndefined();
    });
  });

  it('searching re-queries with the search term and resets to page 1', async () => {
    listApprovalWorklist.mockResolvedValue(paged([item()]));
    const user = userEvent.setup();

    renderWithProviders(<ApprovalsPage />, { route: '/approvals', path: '/approvals' });
    await waitFor(() => expect(screen.getByText('Expense Request')).toBeInTheDocument());

    await user.type(screen.getByPlaceholderText('Search by process or task name'), 'Expense{Enter}');

    await waitFor(() => expect(listApprovalWorklist).toHaveBeenCalledWith(expect.objectContaining({ search: 'Expense', page: 1 })));
  });

  it('reads initial filters from the URL (status/search/page)', async () => {
    listApprovalWorklist.mockResolvedValue(paged([item({ taskStatus: 'Completed' })], 1, 2, 20));

    renderWithProviders(<ApprovalsPage />, { route: '/approvals?status=Completed&search=Expense&page=2', path: '/approvals' });

    await waitFor(() =>
      expect(listApprovalWorklist).toHaveBeenCalledWith(expect.objectContaining({ status: 'Completed', search: 'Expense', page: 2 })),
    );
  });

  it('opening an approval navigates to the existing Task Detail page', async () => {
    listApprovalWorklist.mockResolvedValue(paged([item()]));
    const user = userEvent.setup();

    renderWithProviders(<ApprovalsPage />, { route: '/approvals', path: '/approvals' });
    await screen.findByText('Expense Request');

    await user.click(screen.getByRole('button', { name: 'Open' }));

    expect(navigateMock).toHaveBeenCalledWith('/tasks/task-1');
  });

  it('refetches when Refresh is clicked', async () => {
    listApprovalWorklist.mockResolvedValue(paged([item()]));
    const user = userEvent.setup();

    renderWithProviders(<ApprovalsPage />, { route: '/approvals', path: '/approvals' });
    await screen.findByText('Expense Request');

    await user.click(screen.getByRole('button', { name: /Refresh/i }));
    await waitFor(() => expect(listApprovalWorklist).toHaveBeenCalledTimes(2));
  });

  it('shows a non-approval-progress dash when the item has no approval summary', async () => {
    listApprovalWorklist.mockResolvedValue(paged([item({ approval: null })]));

    renderWithProviders(<ApprovalsPage />, { route: '/approvals', path: '/approvals' });

    await screen.findByText('Expense Request');
    expect(screen.getByText('—')).toBeInTheDocument();
  });
});
