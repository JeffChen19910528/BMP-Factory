import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { Task } from '../../types/task';
import { TasksPage } from './TasksPage';

const { listMyTasks } = vi.hoisted(() => ({ listMyTasks: vi.fn() }));

vi.mock('../../services/taskService', () => ({
  listMyTasks,
  getTask: vi.fn(),
  completeTask: vi.fn(),
  approveTask: vi.fn(),
  rejectTask: vi.fn(),
}));
vi.mock('../../services/userService', () => ({
  listUsers: vi.fn().mockResolvedValue([{ id: 'user-1', username: 'alice', displayName: 'Alice Smith', email: 'alice@bpm.local', departmentId: null, isActive: true }]),
}));

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => vi.fn() };
});

function task(overrides: Partial<Task> = {}): Task {
  return {
    id: 'task-1',
    processInstanceId: 'proc-instance-1',
    nodeId: 'submit',
    nodeName: 'Submit Expense Report',
    assigneeId: 'user-1',
    assigneeRole: null,
    status: 'Pending',
    createdAt: '2026-01-01T00:00:00Z',
    startedAt: null,
    completedAt: null,
    dueAt: null,
    approval: null,
    sla: null,
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('TasksPage', () => {
  it('shows the list of assigned tasks', async () => {
    listMyTasks.mockResolvedValue([task()]);

    renderWithProviders(<TasksPage />);

    await waitFor(() => expect(screen.getByText('Submit Expense Report')).toBeInTheDocument());
    expect(screen.getByText('Pending')).toBeInTheDocument();
    expect(screen.getByText('Alice Smith')).toBeInTheDocument();
  });

  it('shows approval progress for an approval task, and a dash for a plain task', async () => {
    listMyTasks.mockResolvedValue([
      task({ id: 'task-approval', approval: { policy: 'AnyOne', status: 'Pending', requiredCount: 1, approvedCount: 0, rejectedCount: 0, assignments: [] } }),
    ]);

    renderWithProviders(<TasksPage />);

    await waitFor(() => expect(screen.getByText('AnyOne 0/1')).toBeInTheDocument());
  });

  it('shows an empty state when there are no tasks', async () => {
    listMyTasks.mockResolvedValue([]);

    renderWithProviders(<TasksPage />);

    await waitFor(() => expect(screen.getByText(/No tasks assigned to you/i)).toBeInTheDocument());
  });

  it('shows an error state when the list request fails', async () => {
    listMyTasks.mockRejectedValue({
      isAxiosError: true,
      response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } },
    });

    renderWithProviders(<TasksPage />);

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('shows a role-assigned task by its role rather than a user name', async () => {
    listMyTasks.mockResolvedValue([task({ assigneeId: null, assigneeRole: 'Finance' })]);

    renderWithProviders(<TasksPage />);

    await waitFor(() => expect(screen.getByText('Role: Finance')).toBeInTheDocument());
  });

  it('refetches when Refresh is clicked', async () => {
    listMyTasks.mockResolvedValue([task()]);
    const user = userEvent.setup();

    renderWithProviders(<TasksPage />);
    await waitFor(() => expect(screen.getByText('Submit Expense Report')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Refresh/i }));
    await waitFor(() => expect(listMyTasks).toHaveBeenCalledTimes(2));
  });
});
