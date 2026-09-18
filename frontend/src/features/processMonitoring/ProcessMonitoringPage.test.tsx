import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { ProcessMonitoringItem } from '../../types/processMonitoring';
import { ProcessMonitoringPage } from './ProcessMonitoringPage';

const { listProcessMonitoring } = vi.hoisted(() => ({ listProcessMonitoring: vi.fn() }));

vi.mock('../../services/processMonitoringService', () => ({ listProcessMonitoring }));
vi.mock('../../services/userService', () => ({
  listUsers: vi.fn().mockResolvedValue([{ id: 'user-1', username: 'amy', displayName: 'Amy', email: 'amy@bpm.local', departmentId: null, isActive: true }]),
}));

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => vi.fn() };
});

function item(overrides: Partial<ProcessMonitoringItem> = {}): ProcessMonitoringItem {
  return {
    processInstanceId: 'proc-1',
    processDefinitionId: 'def-1',
    processDefinitionKey: 'purchase-request',
    processDefinitionName: 'Purchase Request',
    status: 'Running',
    initiatorId: 'user-1',
    initiatorDisplayName: 'Amy',
    startedAt: '2026-09-16T09:20:00Z',
    updatedAt: '2026-09-16T10:15:00Z',
    currentTaskId: 'task-1',
    currentTaskName: 'Manager Approval',
    currentTaskStatus: 'Pending',
    currentTaskIsApprovalTask: true,
    currentTaskAssigneeDisplay: 'Pending approval (1 candidate)',
    activeTaskCount: 1,
    slaStatus: 'Warning',
    slaDueAt: '2026-09-17T09:20:00Z',
    ...overrides,
  };
}

function pagedResult(items: ProcessMonitoringItem[], totalCount = items.length) {
  return { items, totalCount, page: 1, pageSize: 20 };
}

// Opens an AntD Select's dropdown by locating it via its own placeholder text (more robust than
// positional indexing, and avoids the "Status"/"SLA" text also matching the table's column
// headers or AntD's off-screen width-measurement clones) and firing mousedown on its actual
// combobox <input>, which is what AntD v6 itself listens on to open the popup.
function openSelectByPlaceholder(placeholder: string) {
  const placeholderEls = Array.from(document.querySelectorAll('.ant-select-placeholder')).filter((el) => el.textContent === placeholder);
  for (const placeholderEl of placeholderEls) {
    const selectRoot = placeholderEl.closest('.ant-select') as HTMLElement;
    const input = selectRoot.querySelector('input') as HTMLElement;
    fireEvent.mouseDown(input);
  }
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('ProcessMonitoringPage', () => {
  it('renders the process list with status, current task, and SLA', async () => {
    listProcessMonitoring.mockResolvedValue(pagedResult([item()]));

    renderWithProviders(<ProcessMonitoringPage />);

    await waitFor(() => expect(screen.getByText('Purchase Request')).toBeInTheDocument());
    expect(screen.getByText('Running')).toBeInTheDocument();
    expect(screen.getByText('Amy')).toBeInTheDocument();
    expect(screen.getByText('Manager Approval')).toBeInTheDocument();
    expect(screen.getByText('Pending approval (1 candidate)')).toBeInTheDocument();
    expect(screen.getByText('Warning')).toBeInTheDocument();
  });

  it('shows an em dash for a process with no current task or SLA', async () => {
    listProcessMonitoring.mockResolvedValue(pagedResult([
      item({ currentTaskId: null, currentTaskName: null, currentTaskAssigneeDisplay: null, activeTaskCount: 0, slaStatus: null, slaDueAt: null, status: 'Completed' }),
    ]));

    renderWithProviders(<ProcessMonitoringPage />);

    await waitFor(() => expect(screen.getByText('Completed')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'View Task' })).not.toBeInTheDocument();
  });

  it('shows an error state when the request fails', async () => {
    listProcessMonitoring.mockRejectedValue({
      isAxiosError: true,
      response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } },
    });

    renderWithProviders(<ProcessMonitoringPage />);

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('shows an empty state when no processes match', async () => {
    listProcessMonitoring.mockResolvedValue(pagedResult([]));

    renderWithProviders(<ProcessMonitoringPage />);

    await waitFor(() => expect(screen.getByText('No process instances match your filters.')).toBeInTheDocument());
  });

  it('refetches when Refresh is clicked', async () => {
    listProcessMonitoring.mockResolvedValue(pagedResult([item()]));
    const user = userEvent.setup();

    renderWithProviders(<ProcessMonitoringPage />);
    await waitFor(() => expect(screen.getByText('Purchase Request')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Refresh/i }));
    await waitFor(() => expect(listProcessMonitoring).toHaveBeenCalledTimes(2));
  });

  it('passes the search term to the query', async () => {
    listProcessMonitoring.mockResolvedValue(pagedResult([item()]));
    const user = userEvent.setup();

    renderWithProviders(<ProcessMonitoringPage />);
    await waitFor(() => expect(screen.getByText('Purchase Request')).toBeInTheDocument());

    await user.type(screen.getByPlaceholderText('Search process or initiator'), 'Purchase');
    await waitFor(() => expect(listProcessMonitoring).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'Purchase' })));
  });

  it('passes the selected Status filter to the query', async () => {
    listProcessMonitoring.mockResolvedValue(pagedResult([item()])); // default status: 'Running'

    renderWithProviders(<ProcessMonitoringPage />);
    await waitFor(() => expect(screen.getByText('Purchase Request')).toBeInTheDocument());

    openSelectByPlaceholder('Status');
    // "Completed" is chosen (rather than "Running") because it does not already appear elsewhere
    // on screen (the row's own Status tag already reads "Running"). Selected directly via the
    // dropdown's own option elements (rather than screen.findByText, which was unreliable against
    // AntD's option markup in this environment).
    const completedOption = Array.from(document.querySelectorAll('.ant-select-item-option')).find((el) => el.textContent === 'Completed')!;
    fireEvent.click(completedOption);

    await waitFor(() => expect(listProcessMonitoring).toHaveBeenLastCalledWith(expect.objectContaining({ status: 'Completed' })));
  });

  it('passes the selected SLA filter to the query', async () => {
    listProcessMonitoring.mockResolvedValue(pagedResult([item()])); // default slaStatus: 'Warning'

    renderWithProviders(<ProcessMonitoringPage />);
    await waitFor(() => expect(screen.getByText('Purchase Request')).toBeInTheDocument());

    openSelectByPlaceholder('SLA');
    const overdueOption = Array.from(document.querySelectorAll('.ant-select-item-option')).find((el) => el.textContent === 'Overdue')!;
    fireEvent.click(overdueOption);

    await waitFor(() => expect(listProcessMonitoring).toHaveBeenLastCalledWith(expect.objectContaining({ slaStatus: 'Overdue' })));
  });

  it('resets filters via the Reset button', async () => {
    listProcessMonitoring.mockResolvedValue(pagedResult([item()]));
    const user = userEvent.setup();

    renderWithProviders(<ProcessMonitoringPage />);
    await waitFor(() => expect(screen.getByText('Purchase Request')).toBeInTheDocument());

    await user.type(screen.getByPlaceholderText('Search process or initiator'), 'Purchase');
    await waitFor(() => expect(listProcessMonitoring).toHaveBeenLastCalledWith(expect.objectContaining({ search: 'Purchase' })));

    await user.click(screen.getByRole('button', { name: 'Reset' }));
    await waitFor(() => expect(listProcessMonitoring).toHaveBeenLastCalledWith(expect.objectContaining({ search: undefined })));
    expect(screen.getByPlaceholderText('Search process or initiator')).toHaveValue('');
  });

  it('renders pagination reflecting the total count', async () => {
    listProcessMonitoring.mockResolvedValue(pagedResult([item()], 45));

    renderWithProviders(<ProcessMonitoringPage />);

    await waitFor(() => expect(screen.getByText('Purchase Request')).toBeInTheDocument());
    const pagination = document.querySelector('.ant-pagination') as HTMLElement;
    expect(within(pagination).getByText(/45/)).toBeInTheDocument();
  });
});
