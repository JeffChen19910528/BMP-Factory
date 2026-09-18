import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { PagedResult, ProcessDefinition } from '../../types/process';
import { ProcessDefinitionsPage } from './ProcessDefinitionsPage';

const { listProcessDefinitions, createProcessDefinition } = vi.hoisted(() => ({
  listProcessDefinitions: vi.fn(),
  createProcessDefinition: vi.fn(),
}));

vi.mock('../../services/processService', () => ({
  listProcessDefinitions,
  createProcessDefinition,
}));

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => vi.fn() };
});

function definition(overrides: Partial<ProcessDefinition> = {}): ProcessDefinition {
  return {
    id: 'proc-1',
    key: 'leave-request',
    name: 'Leave Request',
    description: null,
    category: 'HR',
    status: 'Draft',
    currentVersionId: null,
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: 'user-1',
    updatedAt: null,
    ownerUserId: null,
    rowVersion: 'AA==',
    ...overrides,
  };
}

function pagedResult(items: ProcessDefinition[], totalCount = items.length): PagedResult<ProcessDefinition> {
  return { items, totalCount, page: 1, pageSize: 20 };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('ProcessDefinitionsPage', () => {
  it('shows a loading state, then the list of process definitions', async () => {
    listProcessDefinitions.mockResolvedValue(pagedResult([definition()]));

    renderWithProviders(<ProcessDefinitionsPage />);

    await waitFor(() => expect(screen.getByText('Leave Request')).toBeInTheDocument());
    expect(screen.getByText('leave-request')).toBeInTheDocument();
    expect(screen.getByText('HR')).toBeInTheDocument();
  });

  it('shows an empty state when there are no process definitions', async () => {
    listProcessDefinitions.mockResolvedValue(pagedResult([]));

    renderWithProviders(<ProcessDefinitionsPage />);

    await waitFor(() => expect(screen.getByText(/No process definitions yet/i)).toBeInTheDocument());
  });

  it('shows an error state when the list request fails', async () => {
    listProcessDefinitions.mockRejectedValue({
      isAxiosError: true,
      response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } },
    });

    renderWithProviders(<ProcessDefinitionsPage />);

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('re-queries with the search term when the user searches', async () => {
    listProcessDefinitions.mockResolvedValue(pagedResult([definition()]));
    const user = userEvent.setup();

    renderWithProviders(<ProcessDefinitionsPage />);
    await waitFor(() => expect(screen.getByText('Leave Request')).toBeInTheDocument());

    const searchInput = screen.getByPlaceholderText('Search by name or key');
    await user.type(searchInput, 'leave{Enter}');

    await waitFor(() =>
      expect(listProcessDefinitions).toHaveBeenCalledWith(
        expect.objectContaining({ search: 'leave', page: 1 }),
      ),
    );
  });

  it('re-queries with the status filter when changed', async () => {
    listProcessDefinitions.mockResolvedValue(pagedResult([definition()]));
    const user = userEvent.setup();

    renderWithProviders(<ProcessDefinitionsPage />);
    await waitFor(() => expect(screen.getByText('Leave Request')).toBeInTheDocument());

    await user.click(screen.getByText('All statuses'));
    const option = await screen.findByText('Published');
    await user.click(option);

    await waitFor(() =>
      expect(listProcessDefinitions).toHaveBeenCalledWith(expect.objectContaining({ status: 'Published' })),
    );
  });

  it('opens the create modal and creates a process', async () => {
    listProcessDefinitions.mockResolvedValue(pagedResult([]));
    createProcessDefinition.mockResolvedValue(definition({ id: 'new-id', name: 'New Proc' }));
    const user = userEvent.setup();

    renderWithProviders(<ProcessDefinitionsPage />);
    await waitFor(() => expect(screen.getByText(/No process definitions yet/i)).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Create Process/i }));
    const dialog = await screen.findByRole('dialog');

    await user.type(within(dialog).getByLabelText('Name'), 'New Proc');
    await user.type(within(dialog).getByLabelText('Key'), 'new-proc');
    await user.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() =>
      expect(createProcessDefinition).toHaveBeenCalledWith(
        expect.objectContaining({ name: 'New Proc', key: 'new-proc' }),
        expect.anything(),
      ),
    );
  });

  it('shows a validation message when Key has an invalid format', async () => {
    listProcessDefinitions.mockResolvedValue(pagedResult([]));
    const user = userEvent.setup();

    renderWithProviders(<ProcessDefinitionsPage />);
    await waitFor(() => expect(screen.getByText(/No process definitions yet/i)).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Create Process/i }));
    const dialog = await screen.findByRole('dialog');

    await user.type(within(dialog).getByLabelText('Name'), 'Bad Key Proc');
    await user.type(within(dialog).getByLabelText('Key'), 'bad key!');
    await user.click(within(dialog).getByRole('button', { name: 'Create' }));

    expect(await within(dialog).findByText(/Key must contain only letters, digits, - and _/i)).toBeInTheDocument();
    expect(createProcessDefinition).not.toHaveBeenCalled();
  });

  it('shows the backend conflict error when the key is already taken', async () => {
    listProcessDefinitions.mockResolvedValue(pagedResult([]));
    createProcessDefinition.mockRejectedValue({
      isAxiosError: true,
      response: {
        status: 409,
        data: { code: 'PROCESS_DEFINITION_KEY_TAKEN', message: "A process definition with key 'dup' already exists.", traceId: 't-2' },
      },
    });
    const user = userEvent.setup();

    renderWithProviders(<ProcessDefinitionsPage />);
    await waitFor(() => expect(screen.getByText(/No process definitions yet/i)).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Create Process/i }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Name'), 'Dup');
    await user.type(within(dialog).getByLabelText('Key'), 'dup');
    await user.click(within(dialog).getByRole('button', { name: 'Create' }));

    expect(await within(dialog).findByText(/already exists/i)).toBeInTheDocument();
  });
});
