import { screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { ProcessInstanceDetail } from '../../types/processMonitoring';
import type { Task } from '../../types/task';
import { ProcessInstanceDetailPage } from './ProcessInstanceDetailPage';

const { getProcessInstanceDetail } = vi.hoisted(() => ({ getProcessInstanceDetail: vi.fn() }));

vi.mock('../../services/processMonitoringService', () => ({ getProcessInstanceDetail }));
vi.mock('../../services/userService', () => ({
  listUsers: vi.fn().mockResolvedValue([{ id: 'user-1', username: 'amy', displayName: 'Amy', email: 'amy@bpm.local', departmentId: null, isActive: true }]),
}));

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => vi.fn() };
});

function task(overrides: Partial<Task> = {}): Task {
  return {
    id: 'task-1',
    processInstanceId: 'proc-1',
    nodeId: 'first',
    nodeName: 'First Step',
    assigneeId: 'user-1',
    assigneeRole: null,
    status: 'Pending',
    createdAt: '2026-09-16T09:20:00Z',
    startedAt: null,
    completedAt: null,
    dueAt: null,
    approval: null,
    sla: { startedAt: '2026-09-16T09:20:00Z', warningAt: '2026-09-16T09:50:00Z', dueAt: '2026-09-16T10:20:00Z', completedAt: null, status: 'Active' },
    ...overrides,
  };
}

function detail(overrides: Partial<ProcessInstanceDetail> = {}): ProcessInstanceDetail {
  return {
    processInstanceId: 'proc-1',
    processDefinitionId: 'def-1',
    processDefinitionKey: 'purchase-request',
    processDefinitionName: 'Purchase Request',
    status: 'Running',
    initiatorId: 'user-1',
    initiatorDisplayName: 'Amy',
    startedAt: '2026-09-16T09:20:00Z',
    completedAt: null,
    activeTaskCount: 1,
    currentTask: task(),
    tasks: [task()],
    slaSummary: task().sla,
    slaStatus: 'Active',
    workflowProgress: [
      { nodeId: 'start', nodeType: 'Start', displayName: 'Start', state: 'Completed' },
      { nodeId: 'first', nodeType: 'UserTask', displayName: 'First Step', state: 'Current' },
      { nodeId: 'end', nodeType: 'End', displayName: 'End', state: 'Pending' },
    ],
    timeline: [
      { timestamp: '2026-09-16T09:20:00Z', eventType: 'StartProcess', title: 'Process started', description: null, sourceId: 'proc-1', actorId: 'user-1', actorDisplayName: 'Amy' },
    ],
    ...overrides,
  };
}

function renderAt(id = 'proc-1') {
  return renderWithProviders(<ProcessInstanceDetailPage />, { route: `/instances/${id}`, path: '/instances/:processInstanceId' });
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('ProcessInstanceDetailPage', () => {
  it('renders the process summary', async () => {
    getProcessInstanceDetail.mockResolvedValue(detail());

    renderAt();

    await waitFor(() => expect(screen.getAllByText('Purchase Request').length).toBeGreaterThan(0));
    expect(screen.getByText('purchase-request')).toBeInTheDocument();
    expect(screen.getAllByText('Running').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Amy').length).toBeGreaterThan(0);
  });

  it('shows a loading state before data arrives', async () => {
    let resolve!: (value: ProcessInstanceDetail) => void;
    getProcessInstanceDetail.mockReturnValue(new Promise((r) => { resolve = r; }));

    const { container } = renderAt();

    expect(container.querySelector('.ant-skeleton')).not.toBeNull();
    resolve(detail());
    await waitFor(() => expect(container.querySelector('.ant-skeleton')).toBeNull());
  });

  it('shows a 403 error state without redirecting', async () => {
    getProcessInstanceDetail.mockRejectedValue({
      isAxiosError: true,
      response: { status: 403, data: { code: 'PROCESS_INSTANCE_NOT_AUTHORIZED', message: 'You are not authorized to view this process instance.', traceId: 't-1' } },
    });

    renderAt();

    await waitFor(() => expect(screen.getByText('You are not authorized to view this process instance.')).toBeInTheDocument());
  });

  it('shows a 404 error state distinctly from 403', async () => {
    getProcessInstanceDetail.mockRejectedValue({
      isAxiosError: true,
      response: { status: 404, data: undefined },
    });

    renderAt();

    await waitFor(() => expect(screen.getByText('The requested resource was not found.')).toBeInTheDocument());
  });

  it('renders workflow progress steps', async () => {
    getProcessInstanceDetail.mockResolvedValue(detail());

    renderAt();

    await waitFor(() => expect(screen.getByText('Workflow Progress')).toBeInTheDocument());
    expect(screen.getByText('Start')).toBeInTheDocument();
    expect(screen.getByText('End')).toBeInTheDocument();
  });

  it('renders the current task with SLA', async () => {
    getProcessInstanceDetail.mockResolvedValue(detail());

    renderAt();

    await waitFor(() => expect(screen.getByText('Current Task')).toBeInTheDocument());
    expect(screen.getAllByText('Active').length).toBeGreaterThan(0);
  });

  // Phase 7.2.3 hardening regression: the SLA summary tag must render the *derived* bucket
  // (slaStatus, which includes 'Warning') rather than the raw per-task status
  // (slaSummary.status, which has no 'Warning' value) — otherwise a task whose SLA warning has
  // genuinely fired would render as 'Active' here while Process Monitoring's own list correctly
  // shows 'Warning' for the same task.
  it('shows Warning (not Active) when slaStatus is Warning, even though slaSummary.status has no such value', async () => {
    getProcessInstanceDetail.mockResolvedValue(detail({ slaStatus: 'Warning' }));

    renderAt();

    await waitFor(() => expect(screen.getByText('Current Task')).toBeInTheDocument());
    const currentTaskCard = screen.getByText('Current Task').closest('.ant-card') as HTMLElement;
    expect(within(currentTaskCard).getByText('Warning')).toBeInTheDocument();
  });

  it('shows no current task for a completed process', async () => {
    getProcessInstanceDetail.mockResolvedValue(detail({
      status: 'Completed',
      completedAt: '2026-09-16T11:00:00Z',
      activeTaskCount: 0,
      currentTask: null,
      slaSummary: null,
      tasks: [task({ status: 'Completed', completedAt: '2026-09-16T10:00:00Z' })],
    }));

    renderAt();

    await waitFor(() => expect(screen.getByText('No active task — the process has reached a terminal state.')).toBeInTheDocument());
  });

  it('warns rather than guessing when multiple active tasks are reported', async () => {
    getProcessInstanceDetail.mockResolvedValue(detail({ activeTaskCount: 2 }));

    renderAt();

    await waitFor(() => expect(screen.getByText(/2 active tasks were found/)).toBeInTheDocument());
  });

  it('renders task history', async () => {
    getProcessInstanceDetail.mockResolvedValue(detail());

    renderAt();

    await waitFor(() => expect(screen.getByText('Task History')).toBeInTheDocument());
    expect(screen.getAllByText('First Step').length).toBeGreaterThan(0);
  });

  it('renders the activity timeline', async () => {
    getProcessInstanceDetail.mockResolvedValue(detail());

    renderAt();

    await waitFor(() => expect(screen.getByText('Process started')).toBeInTheDocument());
  });

  it('shows an empty state for an empty timeline', async () => {
    getProcessInstanceDetail.mockResolvedValue(detail({ timeline: [] }));

    renderAt();

    await waitFor(() => expect(screen.getByText('No activity yet.')).toBeInTheDocument());
  });

  it('shows Rejected status distinctly for a rejected process', async () => {
    getProcessInstanceDetail.mockResolvedValue(detail({
      status: 'Rejected',
      completedAt: '2026-09-16T11:00:00Z',
      activeTaskCount: 0,
      currentTask: null,
      slaSummary: null,
    }));

    renderAt();

    await waitFor(() => expect(screen.getAllByText('Rejected').length).toBeGreaterThan(0));
  });
});
