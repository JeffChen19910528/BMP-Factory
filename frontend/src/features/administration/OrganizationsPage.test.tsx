import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { useAuthStore } from '../../stores/authStore';
import type { Organization } from '../../types/organization';
import { OrganizationsPage } from './OrganizationsPage';

const { listOrganizations, createOrganization, updateOrganization } = vi.hoisted(() => ({
  listOrganizations: vi.fn(),
  createOrganization: vi.fn(),
  updateOrganization: vi.fn(),
}));

vi.mock('../../services/organizationService', () => ({ listOrganizations, createOrganization, updateOrganization }));

function organization(overrides: Partial<Organization> = {}): Organization {
  return { id: 'org-1', name: 'Acme Corp', parentId: null, rowVersion: 'AAAAAAAAAAE=', ...overrides };
}

beforeEach(() => {
  vi.clearAllMocks();
  useAuthStore.setState({ accessToken: 'token', user: { userId: 'admin-1', displayName: 'Admin', roles: ['Administrator'] } });
});

describe('OrganizationsPage', () => {
  it('shows a loading state, then the list of organizations', async () => {
    listOrganizations.mockResolvedValue([organization()]);
    renderWithProviders(<OrganizationsPage />);

    await waitFor(() => expect(screen.getByText('Acme Corp')).toBeInTheDocument());
  });

  it('shows an empty state when there are no organizations', async () => {
    listOrganizations.mockResolvedValue([]);
    renderWithProviders(<OrganizationsPage />);

    await waitFor(() => expect(screen.getByText(/No organizations match/i)).toBeInTheDocument());
  });

  it('displays hierarchy via the Parent column', async () => {
    listOrganizations.mockResolvedValue([organization(), organization({ id: 'org-2', name: 'Subsidiary', parentId: 'org-1' })]);
    renderWithProviders(<OrganizationsPage />);

    await waitFor(() => expect(screen.getByText('Subsidiary')).toBeInTheDocument());
    const row = screen.getByText('Subsidiary').closest('tr')!;
    expect(within(row).getByText('Acme Corp')).toBeInTheDocument();
  });

  it('creates an organization through the Create modal', async () => {
    listOrganizations.mockResolvedValue([]);
    createOrganization.mockResolvedValue(organization({ id: 'new-id' }));
    const userAction = userEvent.setup();
    renderWithProviders(<OrganizationsPage />);

    await waitFor(() => expect(screen.getByText(/No organizations match/i)).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: /Create Organization/i }));
    const dialog = await screen.findByRole('dialog');

    await userAction.type(within(dialog).getByLabelText('Name'), 'Globex');
    await userAction.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(createOrganization).toHaveBeenCalledWith(expect.objectContaining({ name: 'Globex', parentId: null })));
  });

  it('edits an organization and shows the concurrency-conflict banner on a stale save', async () => {
    listOrganizations.mockResolvedValue([organization()]);
    updateOrganization.mockRejectedValue({
      isAxiosError: true,
      response: { status: 409, data: { code: 'ORGANIZATION_CONCURRENCY_CONFLICT', message: 'Stale.', traceId: 't-1' } },
    });
    const userAction = userEvent.setup();
    renderWithProviders(<OrganizationsPage />);

    await waitFor(() => expect(screen.getByText('Acme Corp')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    await userAction.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText(/modified by another administrator/i)).toBeInTheDocument();
  });
});
