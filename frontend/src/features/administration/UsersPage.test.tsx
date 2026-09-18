import { cleanup, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Modal } from 'antd';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { useAuthStore } from '../../stores/authStore';
import type { Department } from '../../types/department';
import type { User } from '../../types/user';
import { UsersPage } from './UsersPage';

const { listUsers, createUser, updateUser, resetUserPassword, changeMyPassword } = vi.hoisted(() => ({
  listUsers: vi.fn(),
  createUser: vi.fn(),
  updateUser: vi.fn(),
  resetUserPassword: vi.fn(),
  changeMyPassword: vi.fn(),
}));
const { listDepartments } = vi.hoisted(() => ({ listDepartments: vi.fn() }));

vi.mock('../../services/userService', () => ({ listUsers, createUser, updateUser, resetUserPassword, changeMyPassword }));
vi.mock('../../services/departmentService', () => ({ listDepartments }));

function user(overrides: Partial<User> = {}): User {
  return {
    id: 'user-1',
    username: 'alice',
    displayName: 'Alice Smith',
    email: 'alice@example.com',
    departmentId: null,
    isActive: true,
    lastLoginAt: null,
    rowVersion: 'AAAAAAAAAAE=',
    ...overrides,
  };
}

function department(overrides: Partial<Department> = {}): Department {
  return { id: 'dept-1', name: 'Engineering', organizationId: 'org-1', parentId: null, managerUserId: null, rowVersion: 'AAAAAAAAAAE=', ...overrides };
}

beforeEach(() => {
  vi.clearAllMocks();
  listDepartments.mockResolvedValue([department()]);
  useAuthStore.setState({ accessToken: 'token', user: { userId: 'admin-1', displayName: 'Admin', roles: ['Administrator'] } });
});

afterEach(() => {
  // AntD's static Modal.confirm() renders via its own portal outside the React tree RTL
  // unmounts, so a confirm dialog left open by one test (the "disable this user?" prompt never
  // clicks Cancel/Disable) would otherwise bleed into the next test's DOM — see RolesPage.test.tsx
  // for the same precedent.
  Modal.destroyAll();
  cleanup();
});

describe('UsersPage', () => {
  it('shows a loading state, then the list of users', async () => {
    listUsers.mockResolvedValue([user()]);
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText('Alice Smith')).toBeInTheDocument());
    expect(screen.getByText('alice@example.com')).toBeInTheDocument();
  });

  it('shows an empty state when there are no users', async () => {
    listUsers.mockResolvedValue([]);
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText(/No users match/i)).toBeInTheDocument());
  });

  it('shows an error state when the list request fails', async () => {
    listUsers.mockRejectedValue({ isAxiosError: true, response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } } });
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('filters the list by search term', async () => {
    listUsers.mockResolvedValue([user(), user({ id: 'user-2', username: 'bob', displayName: 'Bob Jones', email: 'bob@example.com' })]);
    const userAction = userEvent.setup();
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText('Alice Smith')).toBeInTheDocument());
    await userAction.type(screen.getByPlaceholderText(/Search by username/i), 'bob{Enter}');

    await waitFor(() => expect(screen.queryByText('Alice Smith')).not.toBeInTheDocument());
    expect(screen.getByText('Bob Jones')).toBeInTheDocument();
  });

  it('creates a user through the Create modal', async () => {
    listUsers.mockResolvedValue([]);
    createUser.mockResolvedValue(user({ id: 'new-id' }));
    const userAction = userEvent.setup();
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText(/No users match/i)).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: /Create User/i }));
    const dialog = await screen.findByRole('dialog');

    await userAction.type(within(dialog).getByLabelText('Username'), 'newuser');
    await userAction.type(within(dialog).getByLabelText('Display Name'), 'New User');
    await userAction.type(within(dialog).getByLabelText('Email'), 'new@example.com');
    await userAction.type(within(dialog).getByLabelText('Password'), 'Passw0rd!123');
    await userAction.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() =>
      expect(createUser).toHaveBeenCalledWith(
        expect.objectContaining({ username: 'newuser', displayName: 'New User', email: 'new@example.com' }),
      ),
    );
  });

  it('shows a validation message for an invalid email', async () => {
    listUsers.mockResolvedValue([]);
    const userAction = userEvent.setup();
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText(/No users match/i)).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: /Create User/i }));
    const dialog = await screen.findByRole('dialog');

    await userAction.type(within(dialog).getByLabelText('Username'), 'newuser');
    await userAction.type(within(dialog).getByLabelText('Display Name'), 'New User');
    await userAction.type(within(dialog).getByLabelText('Email'), 'not-an-email');
    await userAction.type(within(dialog).getByLabelText('Password'), 'Passw0rd!123');
    await userAction.click(within(dialog).getByRole('button', { name: 'Create' }));

    expect(await within(dialog).findByText(/valid email address/i)).toBeInTheDocument();
    expect(createUser).not.toHaveBeenCalled();
  });

  it('edits a user through the Edit modal', async () => {
    listUsers.mockResolvedValue([user()]);
    updateUser.mockResolvedValue(user({ displayName: 'Alice Updated', rowVersion: 'AAAAAAAAAAI=' }));
    const userAction = userEvent.setup();
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText('Alice Smith')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');

    const nameInput = within(dialog).getByLabelText('Display Name');
    await userAction.clear(nameInput);
    await userAction.type(nameInput, 'Alice Updated');
    await userAction.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(updateUser).toHaveBeenCalledWith(
        'user-1',
        expect.objectContaining({ displayName: 'Alice Updated', expectedVersion: 'AAAAAAAAAAE=' }),
      ),
    );
  });

  it('shows the concurrency-conflict banner with a Reload action on a stale save', async () => {
    listUsers.mockResolvedValue([user()]);
    updateUser.mockRejectedValue({
      isAxiosError: true,
      response: { status: 409, data: { code: 'USER_CONCURRENCY_CONFLICT', message: 'Stale.', traceId: 't-1' } },
    });
    const userAction = userEvent.setup();
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText('Alice Smith')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    await userAction.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText(/Data was modified by another administrator/i)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Reload' })).toBeInTheDocument();
  });

  it('asks for confirmation before disabling a user', async () => {
    listUsers.mockResolvedValue([user()]);
    const userAction = userEvent.setup();
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText('Alice Smith')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');

    await userAction.click(within(dialog).getByRole('switch'));

    expect((await screen.findAllByText(/Disable this user\?/i)).length).toBeGreaterThan(0);
  });

  it('resets a user password through the Reset Password modal', async () => {
    listUsers.mockResolvedValue([user()]);
    resetUserPassword.mockResolvedValue(user({ rowVersion: 'AAAAAAAAAAI=' }));
    const userAction = userEvent.setup();
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText('Alice Smith')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Reset Password' }));
    const dialog = (await screen.findByText(/Reset Password —/)).closest('.ant-modal') as HTMLElement;

    await userAction.type(within(dialog).getByLabelText('New Password'), 'NewPassw0rd!123');
    await userAction.type(within(dialog).getByLabelText('Confirm Password'), 'NewPassw0rd!123');
    await userAction.click(within(dialog).getByRole('button', { name: 'Reset Password' }));

    await waitFor(() =>
      expect(resetUserPassword).toHaveBeenCalledWith(
        'user-1',
        expect.objectContaining({ newPassword: 'NewPassw0rd!123', expectedVersion: 'AAAAAAAAAAE=' }),
      ),
    );
  });

  it('rejects a Reset Password submission when confirmation does not match', async () => {
    listUsers.mockResolvedValue([user()]);
    const userAction = userEvent.setup();
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText('Alice Smith')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Reset Password' }));
    const dialog = (await screen.findByText(/Reset Password —/)).closest('.ant-modal') as HTMLElement;

    await userAction.type(within(dialog).getByLabelText('New Password'), 'NewPassw0rd!123');
    await userAction.type(within(dialog).getByLabelText('Confirm Password'), 'Mismatch123');
    await userAction.click(within(dialog).getByRole('button', { name: 'Reset Password' }));

    expect(await within(dialog).findByText(/do not match/i)).toBeInTheDocument();
    expect(resetUserPassword).not.toHaveBeenCalled();
  });

  it('shows the concurrency-conflict banner on a stale password reset', async () => {
    listUsers.mockResolvedValue([user()]);
    resetUserPassword.mockRejectedValue({
      isAxiosError: true,
      response: { status: 409, data: { code: 'USER_CONCURRENCY_CONFLICT', message: 'Stale.', traceId: 't-1' } },
    });
    const userAction = userEvent.setup();
    renderWithProviders(<UsersPage />);

    await waitFor(() => expect(screen.getByText('Alice Smith')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Reset Password' }));
    const dialog = (await screen.findByText(/Reset Password —/)).closest('.ant-modal') as HTMLElement;

    await userAction.type(within(dialog).getByLabelText('New Password'), 'NewPassw0rd!123');
    await userAction.type(within(dialog).getByLabelText('Confirm Password'), 'NewPassw0rd!123');
    await userAction.click(within(dialog).getByRole('button', { name: 'Reset Password' }));

    expect(await within(dialog).findByText(/modified by another administrator/i)).toBeInTheDocument();
  });
});
