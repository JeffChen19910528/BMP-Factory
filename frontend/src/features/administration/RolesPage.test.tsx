import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Modal } from 'antd';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { useAuthStore } from '../../stores/authStore';
import type { Role } from '../../types/role';
import type { User } from '../../types/user';
import { RolesPage } from './RolesPage';

const { listRoles, createRole, updateRole, getRoleMembers, assignRole, unassignRole } = vi.hoisted(() => ({
  listRoles: vi.fn(),
  createRole: vi.fn(),
  updateRole: vi.fn(),
  getRoleMembers: vi.fn(),
  assignRole: vi.fn(),
  unassignRole: vi.fn(),
}));
const { listUsers } = vi.hoisted(() => ({ listUsers: vi.fn() }));

vi.mock('../../services/roleService', () => ({ listRoles, createRole, updateRole, getRoleMembers, assignRole, unassignRole }));
vi.mock('../../services/userService', () => ({ listUsers }));

function role(overrides: Partial<Role> = {}): Role {
  return { id: 'role-1', name: 'Manager', memberCount: 1, rowVersion: 'AAAAAAAAAAE=', ...overrides };
}

function user(overrides: Partial<User> = {}): User {
  return { id: 'user-1', username: 'alice', displayName: 'Alice Smith', email: 'alice@example.com', departmentId: null, isActive: true, lastLoginAt: null, rowVersion: 'AAAAAAAAAAE=', ...overrides };
}

beforeEach(() => {
  vi.clearAllMocks();
  listUsers.mockResolvedValue([user(), user({ id: 'user-2', username: 'bob', displayName: 'Bob Jones' })]);
  useAuthStore.setState({ accessToken: 'token', user: { userId: 'admin-1', displayName: 'Admin', roles: ['Administrator'] } });
});

afterEach(() => {
  // AntD's static Modal.confirm() renders via its own portal outside the React tree RTL
  // unmounts, so a confirm dialog left open by one test (e.g. "confirms before unassigning...",
  // which never clicks Remove/Cancel) would otherwise bleed into the next test's DOM.
  Modal.destroyAll();
  cleanup();
});

// role="dialog" doesn't reliably carry an accessible name matching the title text in this AntD
// version's markup, and a leftover Modal.confirm mid-leave-animation can leave a second
// role="dialog" node in the DOM across tests — so locate the actual RoleMembersModal by its own
// title text instead of by accessible name.
async function findRoleMembersDialog(): Promise<HTMLElement> {
  const title = await screen.findByText(/Members of/i);
  const dialog = title.closest('.ant-modal') as HTMLElement | null;
  if (!dialog) throw new Error('RoleMembersModal dialog not found');
  return dialog;
}

describe('RolesPage', () => {
  it('shows a loading state, then the list of roles with member counts', async () => {
    listRoles.mockResolvedValue([role()]);
    renderWithProviders(<RolesPage />);

    await waitFor(() => expect(screen.getByText('Manager')).toBeInTheDocument());
    const row = screen.getByText('Manager').closest('tr')!;
    expect(within(row).getByText('1')).toBeInTheDocument();
  });

  it('shows an empty state when there are no roles', async () => {
    listRoles.mockResolvedValue([]);
    renderWithProviders(<RolesPage />);

    await waitFor(() => expect(screen.getByText(/No roles match/i)).toBeInTheDocument());
  });

  it('creates a role through the Create modal', async () => {
    listRoles.mockResolvedValue([]);
    createRole.mockResolvedValue(role({ id: 'new-id', memberCount: 0 }));
    const userAction = userEvent.setup();
    renderWithProviders(<RolesPage />);

    await waitFor(() => expect(screen.getByText(/No roles match/i)).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: /Create Role/i }));
    const dialog = await screen.findByRole('dialog');

    await userAction.type(within(dialog).getByLabelText('Name'), 'Reviewer');
    await userAction.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(createRole).toHaveBeenCalledWith({ name: 'Reviewer' }));
  });

  it('opens Manage Members and shows the checkbox-style member list', async () => {
    listRoles.mockResolvedValue([role()]);
    getRoleMembers.mockResolvedValue([user()]);
    const userAction = userEvent.setup();
    renderWithProviders(<RolesPage />);

    await waitFor(() => expect(screen.getByText('Manager')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Manage Members' }));

    const dialog = await findRoleMembersDialog();
    await waitFor(() => expect(within(dialog).getByRole('checkbox', { name: /Alice Smith/i })).toBeChecked());
    expect(within(dialog).getByRole('checkbox', { name: /Bob Jones/i })).not.toBeChecked();
  });

  it('assigns a role to a user by checking their checkbox', async () => {
    listRoles.mockResolvedValue([role()]);
    getRoleMembers.mockResolvedValue([user()]);
    assignRole.mockResolvedValue(undefined);
    const userAction = userEvent.setup();
    renderWithProviders(<RolesPage />);

    await waitFor(() => expect(screen.getByText('Manager')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Manage Members' }));
    const dialog = await findRoleMembersDialog();

    await waitFor(() => expect(within(dialog).getByRole('checkbox', { name: /Bob Jones/i })).toBeInTheDocument());
    await userAction.click(within(dialog).getByRole('checkbox', { name: /Bob Jones/i }));

    await waitFor(() => expect(assignRole).toHaveBeenCalledWith({ userId: 'user-2', roleId: 'role-1' }));
  });

  it('confirms before unassigning a role membership', async () => {
    listRoles.mockResolvedValue([role()]);
    getRoleMembers.mockResolvedValue([user()]);
    const userAction = userEvent.setup();
    renderWithProviders(<RolesPage />);

    await waitFor(() => expect(screen.getByText('Manager')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Manage Members' }));
    const dialog = await findRoleMembersDialog();

    await waitFor(() => expect(within(dialog).getByRole('checkbox', { name: /Alice Smith/i })).toBeChecked());
    await userAction.click(within(dialog).getByRole('checkbox', { name: /Alice Smith/i }));

    expect((await screen.findAllByText(/Remove role membership\?/i)).length).toBeGreaterThan(0);
    expect(unassignRole).not.toHaveBeenCalled();
  });

  it('disables the checkbox for removing your own Administrator role', async () => {
    listUsers.mockResolvedValue([user({ id: 'admin-1', displayName: 'Admin', username: 'admin' })]);
    listRoles.mockResolvedValue([role({ id: 'admin-role', name: 'Administrator', memberCount: 1 })]);
    getRoleMembers.mockResolvedValue([user({ id: 'admin-1', displayName: 'Admin', username: 'admin' })]);
    const userAction = userEvent.setup();
    renderWithProviders(<RolesPage />);

    await waitFor(() => expect(screen.getByText('Administrator')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Manage Members' }));
    const dialog = await findRoleMembersDialog();

    await waitFor(() => expect(within(dialog).getByRole('checkbox', { name: /Admin/i })).toBeChecked());
    expect(within(dialog).getByRole('checkbox', { name: /Admin/i })).toBeDisabled();
  });

  it('renames a role through the Rename modal', async () => {
    listRoles.mockResolvedValue([role()]);
    updateRole.mockResolvedValue(role({ name: 'Senior Manager' }));
    const userAction = userEvent.setup();
    renderWithProviders(<RolesPage />);

    await waitFor(() => expect(screen.getByText('Manager')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Rename' }));
    const dialog = (await screen.findByText('Rename Role')).closest('.ant-modal') as HTMLElement;

    const nameInput = within(dialog).getByLabelText('Name');
    await userAction.clear(nameInput);
    await userAction.type(nameInput, 'Senior Manager');
    await userAction.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(updateRole).toHaveBeenCalledWith('role-1', expect.objectContaining({ name: 'Senior Manager', expectedVersion: 'AAAAAAAAAAE=' })),
    );
  });

  it('shows the concurrency-conflict banner on a stale rename', async () => {
    listRoles.mockResolvedValue([role()]);
    updateRole.mockRejectedValue({
      isAxiosError: true,
      response: { status: 409, data: { code: 'ROLE_CONCURRENCY_CONFLICT', message: 'Stale.', traceId: 't-1' } },
    });
    const userAction = userEvent.setup();
    renderWithProviders(<RolesPage />);

    await waitFor(() => expect(screen.getByText('Manager')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Rename' }));
    const dialog = (await screen.findByText('Rename Role')).closest('.ant-modal') as HTMLElement;
    await userAction.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText(/modified by another administrator/i)).toBeInTheDocument();
  });

  it('disables the Rename action for the Administrator role', async () => {
    listRoles.mockResolvedValue([role({ id: 'admin-role', name: 'Administrator', memberCount: 1 })]);
    renderWithProviders(<RolesPage />);

    await waitFor(() => expect(screen.getByText('Administrator')).toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Rename' })).toBeDisabled();
  });
});
