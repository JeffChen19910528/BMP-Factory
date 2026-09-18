import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { useAuthStore } from '../../stores/authStore';
import type { Department } from '../../types/department';
import type { Organization } from '../../types/organization';
import type { User } from '../../types/user';
import { DepartmentsPage } from './DepartmentsPage';

const { listDepartments, createDepartment, updateDepartment } = vi.hoisted(() => ({
  listDepartments: vi.fn(),
  createDepartment: vi.fn(),
  updateDepartment: vi.fn(),
}));
const { listOrganizations } = vi.hoisted(() => ({ listOrganizations: vi.fn() }));
const { listUsers } = vi.hoisted(() => ({ listUsers: vi.fn() }));

vi.mock('../../services/departmentService', () => ({ listDepartments, createDepartment, updateDepartment }));
vi.mock('../../services/organizationService', () => ({ listOrganizations }));
vi.mock('../../services/userService', () => ({ listUsers }));

function department(overrides: Partial<Department> = {}): Department {
  return { id: 'dept-1', name: 'Engineering', organizationId: 'org-1', parentId: null, managerUserId: null, rowVersion: 'AAAAAAAAAAE=', ...overrides };
}

function organization(overrides: Partial<Organization> = {}): Organization {
  return { id: 'org-1', name: 'Acme Corp', parentId: null, rowVersion: 'AAAAAAAAAAE=', ...overrides };
}

function user(overrides: Partial<User> = {}): User {
  return { id: 'user-1', username: 'alice', displayName: 'Alice Smith', email: 'alice@example.com', departmentId: null, isActive: true, lastLoginAt: null, rowVersion: 'AAAAAAAAAAE=', ...overrides };
}

beforeEach(() => {
  vi.clearAllMocks();
  listOrganizations.mockResolvedValue([organization()]);
  listUsers.mockResolvedValue([user()]);
  useAuthStore.setState({ accessToken: 'token', user: { userId: 'admin-1', displayName: 'Admin', roles: ['Administrator'] } });
});

describe('DepartmentsPage', () => {
  it('shows a loading state, then the list of departments', async () => {
    listDepartments.mockResolvedValue([department()]);
    renderWithProviders(<DepartmentsPage />);

    await waitFor(() => expect(screen.getByText('Engineering')).toBeInTheDocument());
  });

  it('shows an empty state when there are no departments', async () => {
    listDepartments.mockResolvedValue([]);
    renderWithProviders(<DepartmentsPage />);

    await waitFor(() => expect(screen.getByText(/No departments match/i)).toBeInTheDocument());
  });

  it('displays hierarchy via the Parent column', async () => {
    listDepartments.mockResolvedValue([department(), department({ id: 'dept-2', name: 'Backend', parentId: 'dept-1' })]);
    renderWithProviders(<DepartmentsPage />);

    await waitFor(() => expect(screen.getByText('Backend')).toBeInTheDocument());
    const row = screen.getByText('Backend').closest('tr')!;
    expect(within(row).getByText('Engineering')).toBeInTheDocument();
  });

  it('creates a department through the Create modal', async () => {
    listDepartments.mockResolvedValue([]);
    createDepartment.mockResolvedValue(department({ id: 'new-id' }));
    const userAction = userEvent.setup();
    renderWithProviders(<DepartmentsPage />);

    await waitFor(() => expect(screen.getByText(/No departments match/i)).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: /Create Department/i }));
    const dialog = await screen.findByRole('dialog');

    await userAction.type(within(dialog).getByLabelText('Name'), 'Sales');
    await userAction.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(createDepartment).toHaveBeenCalledWith(expect.objectContaining({ name: 'Sales', organizationId: 'org-1' })));
  });

  it('edits a department and shows the concurrency-conflict banner on a stale save', async () => {
    listDepartments.mockResolvedValue([department()]);
    updateDepartment.mockRejectedValue({
      isAxiosError: true,
      response: { status: 409, data: { code: 'DEPARTMENT_CONCURRENCY_CONFLICT', message: 'Stale.', traceId: 't-1' } },
    });
    const userAction = userEvent.setup();
    renderWithProviders(<DepartmentsPage />);

    await waitFor(() => expect(screen.getByText('Engineering')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    await userAction.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText(/Data was modified by another administrator/i)).toBeInTheDocument();
  });

  it('confirms before changing the department manager', async () => {
    listDepartments.mockResolvedValue([department()]);
    const userAction = userEvent.setup();
    renderWithProviders(<DepartmentsPage />);

    await waitFor(() => expect(screen.getByText('Engineering')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');

    const comboboxes = within(dialog).getAllByRole('combobox');
    await userAction.click(comboboxes[comboboxes.length - 1]);
    const option = await screen.findByText('Alice Smith');
    await userAction.click(option);
    await userAction.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect((await screen.findAllByText(/Change department manager\?/i)).length).toBeGreaterThan(0);
  });
});
