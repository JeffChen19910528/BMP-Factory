import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { FormDefinition } from '../../types/form';
import { FormDefinitionsPage } from './FormDefinitionsPage';

const { listFormDefinitions, createFormDefinition } = vi.hoisted(() => ({
  listFormDefinitions: vi.fn(),
  createFormDefinition: vi.fn(),
}));

vi.mock('../../services/formDefinitionService', () => ({
  listFormDefinitions,
  createFormDefinition,
}));

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => vi.fn() };
});

function definition(overrides: Partial<FormDefinition> = {}): FormDefinition {
  return {
    id: 'form-1',
    key: 'purchase-request',
    name: 'Purchase Request',
    description: null,
    category: 'Procurement',
    status: 'Draft',
    currentVersionId: null,
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: 'user-1',
    updatedAt: null,
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('FormDefinitionsPage', () => {
  it('shows a loading state, then the list of form definitions', async () => {
    listFormDefinitions.mockResolvedValue([definition()]);

    renderWithProviders(<FormDefinitionsPage />);

    await waitFor(() => expect(screen.getByText('Purchase Request')).toBeInTheDocument());
    expect(screen.getByText('purchase-request')).toBeInTheDocument();
    expect(screen.getByText('Procurement')).toBeInTheDocument();
  });

  it('shows an empty state when there are no form definitions', async () => {
    listFormDefinitions.mockResolvedValue([]);

    renderWithProviders(<FormDefinitionsPage />);

    await waitFor(() => expect(screen.getByText(/No form definitions yet/i)).toBeInTheDocument());
  });

  it('shows an error state when the list request fails', async () => {
    listFormDefinitions.mockRejectedValue({
      isAxiosError: true,
      response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } },
    });

    renderWithProviders(<FormDefinitionsPage />);

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('refetches when Refresh is clicked', async () => {
    listFormDefinitions.mockResolvedValue([definition()]);
    const user = userEvent.setup();

    renderWithProviders(<FormDefinitionsPage />);
    await waitFor(() => expect(screen.getByText('Purchase Request')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Refresh/i }));
    await waitFor(() => expect(listFormDefinitions).toHaveBeenCalledTimes(2));
  });

  it('opens the create modal and creates a form', async () => {
    listFormDefinitions.mockResolvedValue([]);
    createFormDefinition.mockResolvedValue(definition({ id: 'new-id', name: 'New Form' }));
    const user = userEvent.setup();

    renderWithProviders(<FormDefinitionsPage />);
    await waitFor(() => expect(screen.getByText(/No form definitions yet/i)).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Create Form/i }));
    const dialog = await screen.findByRole('dialog');

    await user.type(within(dialog).getByLabelText('Name'), 'New Form');
    await user.type(within(dialog).getByLabelText('Key'), 'new-form');
    await user.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() =>
      expect(createFormDefinition).toHaveBeenCalledWith(expect.objectContaining({ name: 'New Form', key: 'new-form' }), expect.anything()),
    );
  });

  it('shows a validation message when Key has an invalid format', async () => {
    listFormDefinitions.mockResolvedValue([]);
    const user = userEvent.setup();

    renderWithProviders(<FormDefinitionsPage />);
    await waitFor(() => expect(screen.getByText(/No form definitions yet/i)).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Create Form/i }));
    const dialog = await screen.findByRole('dialog');

    await user.type(within(dialog).getByLabelText('Name'), 'Bad Key Form');
    await user.type(within(dialog).getByLabelText('Key'), 'bad key!');
    await user.click(within(dialog).getByRole('button', { name: 'Create' }));

    expect(await within(dialog).findByText(/Key must contain only letters, digits, - and _/i)).toBeInTheDocument();
    expect(createFormDefinition).not.toHaveBeenCalled();
  });

  it('shows the backend conflict error when the key is already taken', async () => {
    listFormDefinitions.mockResolvedValue([]);
    createFormDefinition.mockRejectedValue({
      isAxiosError: true,
      response: {
        status: 409,
        data: { code: 'FORM_DEFINITION_KEY_TAKEN', message: "A form definition with key 'dup' already exists.", traceId: 't-2' },
      },
    });
    const user = userEvent.setup();

    renderWithProviders(<FormDefinitionsPage />);
    await waitFor(() => expect(screen.getByText(/No form definitions yet/i)).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Create Form/i }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByLabelText('Name'), 'Dup');
    await user.type(within(dialog).getByLabelText('Key'), 'dup');
    await user.click(within(dialog).getByRole('button', { name: 'Create' }));

    expect(await within(dialog).findByText(/already exists/i)).toBeInTheDocument();
  });
});
