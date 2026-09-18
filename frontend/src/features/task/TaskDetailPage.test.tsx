import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { useAuthStore } from '../../stores/authStore';
import type { Task } from '../../types/task';
import type { FormInstance } from '../../types/formInstance';
import type { FormVersion } from '../../types/form';
import { TaskDetailPage } from './TaskDetailPage';

const { getTask, completeTask, approveTask, rejectTask, returnTask, delegateTask, transferTask, addApprover } = vi.hoisted(() => ({
  getTask: vi.fn(),
  completeTask: vi.fn(),
  approveTask: vi.fn(),
  rejectTask: vi.fn(),
  returnTask: vi.fn(),
  delegateTask: vi.fn(),
  transferTask: vi.fn(),
  addApprover: vi.fn(),
}));
const { listFormInstancesByProcessInstance, getFormInstance, getFormInstanceData, saveFormInstanceData, submitFormInstance, listAttachments, uploadAttachment, deleteAttachment } = vi.hoisted(() => ({
  listFormInstancesByProcessInstance: vi.fn(),
  getFormInstance: vi.fn(),
  getFormInstanceData: vi.fn(),
  saveFormInstanceData: vi.fn(),
  submitFormInstance: vi.fn(),
  listAttachments: vi.fn(),
  uploadAttachment: vi.fn(),
  deleteAttachment: vi.fn(),
}));
const { listFormVersions } = vi.hoisted(() => ({ listFormVersions: vi.fn() }));

vi.mock('../../services/taskService', () => ({ getTask, completeTask, approveTask, rejectTask, returnTask, delegateTask, transferTask, addApprover, listMyTasks: vi.fn() }));
vi.mock('../../services/formInstanceService', () => ({
  listFormInstancesByProcessInstance,
  getFormInstance,
  getFormInstanceData,
  saveFormInstanceData,
  submitFormInstance,
  listAttachments,
  uploadAttachment,
  deleteAttachment,
  attachmentDownloadUrl: (id: string) => `/api/attachments/${id}`,
  createFormInstance: vi.fn(),
  cancelFormInstance: vi.fn(),
}));
vi.mock('../../services/formDefinitionService', () => ({ listFormVersions }));
const { listUsers } = vi.hoisted(() => ({ listUsers: vi.fn() }));
vi.mock('../../services/userService', () => ({ listUsers }));
vi.mock('../../services/departmentService', () => ({ listDepartments: vi.fn().mockResolvedValue([]) }));

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useParams: () => ({ id: 'task-1' }), useNavigate: () => vi.fn() };
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

function instance(overrides: Partial<FormInstance> = {}): FormInstance {
  return {
    id: 'instance-1',
    formDefinitionId: 'form-def-1',
    formVersionId: 'form-version-1',
    processInstanceId: 'proc-instance-1',
    taskInstanceId: 'task-1',
    createdByUserId: 'user-1',
    status: 'Draft',
    ...overrides,
  };
}

function version(overrides: Partial<FormVersion> = {}): FormVersion {
  return {
    id: 'form-version-1',
    formDefinitionId: 'form-def-1',
    versionNumber: 1,
    status: 'Published',
    schema: { fields: [{ key: 'amount', type: 'Number', label: 'Amount', required: true }] },
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: null,
    publishedAt: '2026-01-01T00:00:00Z',
    publishedBy: null,
    rowVersion: 'AAAAAAAAAAE=',
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  listAttachments.mockResolvedValue([]);
  listUsers.mockResolvedValue([
    { id: 'user-1', username: 'alice', displayName: 'Alice Smith', email: 'alice@bpm.local', departmentId: null, isActive: true },
    { id: 'user-2', username: 'bob', displayName: 'Bob Jones', email: 'bob@bpm.local', departmentId: null, isActive: true },
  ]);
  useAuthStore.setState({ accessToken: 'token', user: { userId: 'user-1', displayName: 'Alice Smith', roles: [] } });
});

describe('TaskDetailPage — SLA display (Phase 6.3)', () => {
  it('shows the SLA section with status/started/warning/due when the task has an applicable SLA', async () => {
    getTask.mockResolvedValue(task({
      dueAt: '2026-01-02T10:00:00Z',
      sla: { startedAt: '2026-01-01T10:00:00Z', warningAt: '2026-01-01T22:00:00Z', dueAt: '2026-01-02T10:00:00Z', completedAt: null, status: 'Active' },
    }));
    listFormInstancesByProcessInstance.mockResolvedValue([]);

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await waitFor(() => expect(screen.getByText('SLA')).toBeInTheDocument());
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.getByText('Started')).toBeInTheDocument();
    expect(screen.getByText('Warning At')).toBeInTheDocument();
    expect(screen.getByText('Due At')).toBeInTheDocument();
  });

  it('shows Completed status and a completed timestamp for a resolved SLA', async () => {
    getTask.mockResolvedValue(task({
      status: 'Completed',
      sla: { startedAt: '2026-01-01T10:00:00Z', warningAt: '2026-01-01T22:00:00Z', dueAt: '2026-01-02T10:00:00Z', completedAt: '2026-01-01T15:00:00Z', status: 'Completed' },
    }));
    listFormInstancesByProcessInstance.mockResolvedValue([]);

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await waitFor(() => expect(screen.getByText('SLA')).toBeInTheDocument());
    const slaSection = screen.getByText('SLA').closest('.ant-descriptions') as HTMLElement;
    expect(within(slaSection).getAllByText('Completed').length).toBeGreaterThan(0);
    expect(within(slaSection).queryByText('—')).not.toBeInTheDocument();
  });

  it('shows Overdue status for a task whose SLA has been transitioned by the scheduler (Phase 6.4)', async () => {
    getTask.mockResolvedValue(task({
      sla: { startedAt: '2026-01-01T10:00:00Z', warningAt: '2026-01-01T22:00:00Z', dueAt: '2026-01-02T10:00:00Z', completedAt: null, status: 'Overdue' },
    }));
    listFormInstancesByProcessInstance.mockResolvedValue([]);

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await waitFor(() => expect(screen.getByText('SLA')).toBeInTheDocument());
    const slaSection = screen.getByText('SLA').closest('.ant-descriptions') as HTMLElement;
    expect(within(slaSection).getByText('Overdue')).toBeInTheDocument();
  });

  it('does not show an SLA section when the task has no applicable SLA', async () => {
    getTask.mockResolvedValue(task({ sla: null }));
    listFormInstancesByProcessInstance.mockResolvedValue([]);

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await waitFor(() => expect(screen.getByText('This task has no form attached.')).toBeInTheDocument());
    expect(screen.queryByText('SLA')).not.toBeInTheDocument();
  });
});

describe('TaskDetailPage — plain task (no form)', () => {
  it('shows a Complete Task button and completes it', async () => {
    getTask.mockResolvedValue(task());
    listFormInstancesByProcessInstance.mockResolvedValue([]);
    completeTask.mockResolvedValue(task({ status: 'Completed' }));
    const user = userEvent.setup();

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await waitFor(() => expect(screen.getByText('This task has no form attached.')).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: 'Complete Task' }));

    await waitFor(() => expect(completeTask).toHaveBeenCalledWith('task-1'));
  });

  it('disables Complete Task for a task that is not actionable', async () => {
    getTask.mockResolvedValue(task({ status: 'Completed' }));
    listFormInstancesByProcessInstance.mockResolvedValue([]);

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await waitFor(() => expect(screen.getByRole('button', { name: 'Complete Task' })).toBeDisabled());
  });
});

describe('TaskDetailPage — approval task', () => {
  it('shows Approve/Reject actions instead of a form', async () => {
    getTask.mockResolvedValue(
      task({ approval: { policy: 'AnyOne', status: 'Pending', requiredCount: 1, approvedCount: 0, rejectedCount: 0, assignments: [] } }),
    );
    const user = userEvent.setup();
    approveTask.mockResolvedValue(task({ status: 'Completed' }));

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await waitFor(() => expect(screen.getByRole('button', { name: 'Approve' })).toBeInTheDocument());
    await user.click(screen.getByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(approveTask).toHaveBeenCalledWith('task-1'));
  });

  it('returns the task via the Return action', async () => {
    getTask.mockResolvedValue(
      task({ approval: { policy: 'AnyOne', status: 'Pending', requiredCount: 1, approvedCount: 0, rejectedCount: 0, assignments: [] } }),
    );
    returnTask.mockResolvedValue(task({ status: 'Returned' }));
    const user = userEvent.setup();

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await user.click(await screen.findByRole('button', { name: 'Return' }));
    await waitFor(() => expect(returnTask).toHaveBeenCalledWith('task-1'));
  });

  it('delegates the task to a selected user', async () => {
    getTask.mockResolvedValue(
      task({ approval: { policy: 'AnyOne', status: 'Pending', requiredCount: 1, approvedCount: 0, rejectedCount: 0, assignments: [] } }),
    );
    delegateTask.mockResolvedValue(task());
    const user = userEvent.setup();

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await user.click(await screen.findByRole('button', { name: 'Delegate' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('combobox'));
    await user.click(await screen.findByText('Bob Jones'));
    await user.click(within(dialog).getByRole('button', { name: 'OK' }));

    await waitFor(() => expect(delegateTask).toHaveBeenCalledWith('task-1', { delegateToUserId: 'user-2' }));
  });

  it('transfers the task to a selected user with an optional reason', async () => {
    getTask.mockResolvedValue(
      task({ approval: { policy: 'AnyOne', status: 'Pending', requiredCount: 1, approvedCount: 0, rejectedCount: 0, assignments: [] } }),
    );
    transferTask.mockResolvedValue(task());
    const user = userEvent.setup();

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await user.click(await screen.findByRole('button', { name: 'Transfer' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('combobox'));
    await user.click(await screen.findByText('Bob Jones'));
    await user.type(within(dialog).getByPlaceholderText('Reason (optional)'), 'Out of office');
    await user.click(within(dialog).getByRole('button', { name: 'OK' }));

    await waitFor(() => expect(transferTask).toHaveBeenCalledWith('task-1', { newUserId: 'user-2', reason: 'Out of office' }));
  });

  it('hides Add Approver for a non-Administrator', async () => {
    getTask.mockResolvedValue(
      task({ approval: { policy: 'AnyOne', status: 'Pending', requiredCount: 1, approvedCount: 0, rejectedCount: 0, assignments: [] } }),
    );

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await screen.findByRole('button', { name: 'Approve' });
    expect(screen.queryByRole('button', { name: 'Add Approver' })).not.toBeInTheDocument();
  });

  it('shows Add Approver for an Administrator and adds one', async () => {
    useAuthStore.setState({ accessToken: 'token', user: { userId: 'admin-1', displayName: 'Admin', roles: ['Administrator'] } });
    getTask.mockResolvedValue(
      task({ approval: { policy: 'AnyOne', status: 'Pending', requiredCount: 1, approvedCount: 0, rejectedCount: 0, assignments: [] } }),
    );
    addApprover.mockResolvedValue(task());
    const user = userEvent.setup();

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await user.click(await screen.findByRole('button', { name: 'Add Approver' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('combobox'));
    await user.click(await screen.findByText('Bob Jones'));
    await user.click(within(dialog).getByRole('button', { name: 'OK' }));

    await waitFor(() => expect(addApprover).toHaveBeenCalledWith('task-1', 'user-2'));
  });

  it('disables all approval action buttons when the task is not actionable', async () => {
    getTask.mockResolvedValue(
      task({
        status: 'Completed',
        approval: { policy: 'AnyOne', status: 'Approved', requiredCount: 1, approvedCount: 1, rejectedCount: 0, assignments: [] },
      }),
    );

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await screen.findByRole('button', { name: 'Approve' });
    expect(screen.getByRole('button', { name: 'Approve' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Reject' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Return' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Delegate' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Transfer' })).toBeDisabled();
  });

  it('surfaces a backend rejection (e.g. APPROVAL_NOT_ASSIGNED) via the shared error alert', async () => {
    getTask.mockResolvedValue(
      task({ approval: { policy: 'AnyOne', status: 'Pending', requiredCount: 1, approvedCount: 0, rejectedCount: 0, assignments: [] } }),
    );
    approveTask.mockRejectedValue({
      isAxiosError: true,
      response: { status: 403, data: { code: 'APPROVAL_NOT_ASSIGNED', message: 'You are not an approver on this task.', traceId: 't-1' } },
    });
    const user = userEvent.setup();

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await user.click(await screen.findByRole('button', { name: 'Approve' }));
    expect(await screen.findByText('You are not an approver on this task.')).toBeInTheDocument();
  });
});

describe('TaskDetailPage — form-bound task (Form Runtime)', () => {
  it('resolves the task-bound FormInstance and renders the form from its published schema', async () => {
    getTask.mockResolvedValue(task());
    listFormInstancesByProcessInstance.mockResolvedValue([instance(), instance({ id: 'other', taskInstanceId: 'other-task' })]);
    getFormInstance.mockResolvedValue(instance());
    getFormInstanceData.mockResolvedValue({ formInstanceId: 'instance-1', data: {}, version: 'AAAAAAAAAAE=' });
    listFormVersions.mockResolvedValue([version()]);

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await waitFor(() => expect(screen.getByText('Amount')).toBeInTheDocument());
  });

  it('saves a draft with the expected version and shows a success message', async () => {
    getTask.mockResolvedValue(task());
    listFormInstancesByProcessInstance.mockResolvedValue([instance()]);
    getFormInstance.mockResolvedValue(instance());
    getFormInstanceData.mockResolvedValue({ formInstanceId: 'instance-1', data: {}, version: 'AAAAAAAAAAE=' });
    listFormVersions.mockResolvedValue([version()]);
    saveFormInstanceData.mockResolvedValue({ formInstanceId: 'instance-1', data: { amount: 100 }, version: 'AAAAAAAAAAI=' });
    const user = userEvent.setup();

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await screen.findByText('Amount');
    await user.type(screen.getByRole('spinbutton'), '100');
    await user.click(screen.getByRole('button', { name: 'Save Draft' }));

    await waitFor(() => expect(saveFormInstanceData).toHaveBeenCalledWith('instance-1', expect.objectContaining({ expectedVersion: 'AAAAAAAAAAE=' })));
  });

  it('submitting saves first when dirty, then submits, completing the task', async () => {
    getTask.mockResolvedValue(task());
    listFormInstancesByProcessInstance.mockResolvedValue([instance()]);
    getFormInstance.mockResolvedValue(instance());
    getFormInstanceData.mockResolvedValue({ formInstanceId: 'instance-1', data: {}, version: 'AAAAAAAAAAE=' });
    listFormVersions.mockResolvedValue([version()]);
    saveFormInstanceData.mockResolvedValue({ formInstanceId: 'instance-1', data: { amount: 100 }, version: 'AAAAAAAAAAI=' });
    submitFormInstance.mockResolvedValue(instance({ status: 'Submitted' }));
    const user = userEvent.setup();

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await screen.findByText('Amount');
    await user.type(screen.getByRole('spinbutton'), '100');
    await user.click(screen.getByRole('button', { name: 'Submit' }));

    await waitFor(() => expect(saveFormInstanceData).toHaveBeenCalled());
    await waitFor(() => expect(submitFormInstance).toHaveBeenCalledWith('instance-1'));
  });

  it('renders the form read-only when the FormInstance is already Submitted', async () => {
    getTask.mockResolvedValue(task({ status: 'Completed' }));
    listFormInstancesByProcessInstance.mockResolvedValue([instance({ status: 'Submitted' })]);
    getFormInstance.mockResolvedValue(instance({ status: 'Submitted' }));
    getFormInstanceData.mockResolvedValue({ formInstanceId: 'instance-1', data: { amount: 100 }, version: 'AAAAAAAAAAE=' });
    listFormVersions.mockResolvedValue([version()]);

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await screen.findByText('Amount');
    expect(screen.queryByRole('button', { name: 'Submit' })).not.toBeInTheDocument();
    expect(screen.getByRole('spinbutton')).toBeDisabled();
  });
});

const CONFLICT_ERROR = {
  isAxiosError: true,
  response: {
    status: 409,
    data: { code: 'FORM_CONCURRENCY_CONFLICT', message: 'This form was modified by another request. Reload and retry.', traceId: 't-1' },
  },
};

describe('TaskDetailPage — 409 concurrency conflict', () => {
  it('shows a dedicated conflict banner with a Reload Latest action instead of silently overwriting', async () => {
    getTask.mockResolvedValue(task());
    listFormInstancesByProcessInstance.mockResolvedValue([instance()]);
    getFormInstance.mockResolvedValue(instance());
    getFormInstanceData.mockResolvedValue({ formInstanceId: 'instance-1', data: {}, version: 'AAAAAAAAAAE=' });
    listFormVersions.mockResolvedValue([version()]);
    saveFormInstanceData.mockRejectedValue(CONFLICT_ERROR);
    const user = userEvent.setup();

    renderWithProviders(<TaskDetailPage />, { route: '/tasks/task-1', path: '/tasks/:id' });

    await screen.findByText('Amount');
    await user.type(screen.getByRole('spinbutton'), '100');
    await user.click(screen.getByRole('button', { name: 'Save Draft' }));

    expect(await screen.findByText('This form was modified by another user')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reload Latest' })).toBeInTheDocument();
  });
});
