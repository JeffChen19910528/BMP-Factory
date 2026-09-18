import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../test/renderWithProviders';
import { useAuthStore } from '../stores/authStore';
import { ChangePasswordModal } from './ChangePasswordModal';

const { changeMyPassword } = vi.hoisted(() => ({ changeMyPassword: vi.fn() }));

vi.mock('../services/userService', () => ({ changeMyPassword }));

beforeEach(() => {
  vi.clearAllMocks();
  useAuthStore.setState({ accessToken: 'token', user: { userId: 'user-1', displayName: 'Alice', roles: [] } });
});

// Phase 9 Part 17-19 — self-service change password. The request never carries a userId; the
// backend derives the caller from the JWT alone (see ChangePasswordRequest's own type — only
// currentPassword/newPassword).
describe('ChangePasswordModal', () => {
  it('submits current and new password and shows a success message', async () => {
    changeMyPassword.mockResolvedValue(undefined);
    const userAction = userEvent.setup();
    renderWithProviders(<ChangePasswordModal open onClose={() => {}} />);

    const dialog = await screen.findByRole('dialog');
    await userAction.type(screen.getByLabelText('Current Password'), 'OldPassw0rd!');
    await userAction.type(screen.getByLabelText('New Password'), 'NewPassw0rd!123');
    await userAction.type(screen.getByLabelText('Confirm New Password'), 'NewPassw0rd!123');
    await userAction.click(within(dialog).getByRole('button', { name: 'Change Password' }));

    await waitFor(() =>
      expect(changeMyPassword).toHaveBeenCalledWith({ currentPassword: 'OldPassw0rd!', newPassword: 'NewPassw0rd!123' }),
    );
    expect(await screen.findByText(/password has been changed/i)).toBeInTheDocument();
  });

  it('rejects submission when the confirmation does not match', async () => {
    const userAction = userEvent.setup();
    renderWithProviders(<ChangePasswordModal open onClose={() => {}} />);

    const dialog = await screen.findByRole('dialog');
    await userAction.type(screen.getByLabelText('Current Password'), 'OldPassw0rd!');
    await userAction.type(screen.getByLabelText('New Password'), 'NewPassw0rd!123');
    await userAction.type(screen.getByLabelText('Confirm New Password'), 'Mismatch123');
    await userAction.click(within(dialog).getByRole('button', { name: 'Change Password' }));

    expect(await screen.findByText(/do not match/i)).toBeInTheDocument();
    expect(changeMyPassword).not.toHaveBeenCalled();
  });

  it('shows an error when the current password is wrong', async () => {
    changeMyPassword.mockRejectedValue({
      isAxiosError: true,
      response: { status: 400, data: { code: 'INVALID_CURRENT_PASSWORD', message: 'Current password is incorrect.', traceId: 't-1' } },
    });
    const userAction = userEvent.setup();
    renderWithProviders(<ChangePasswordModal open onClose={() => {}} />);

    const dialog = await screen.findByRole('dialog');
    await userAction.type(screen.getByLabelText('Current Password'), 'WrongPassword!');
    await userAction.type(screen.getByLabelText('New Password'), 'NewPassw0rd!123');
    await userAction.type(screen.getByLabelText('Confirm New Password'), 'NewPassw0rd!123');
    await userAction.click(within(dialog).getByRole('button', { name: 'Change Password' }));

    expect(await within(dialog).findByText(/Current password is incorrect/i)).toBeInTheDocument();
  });
});
