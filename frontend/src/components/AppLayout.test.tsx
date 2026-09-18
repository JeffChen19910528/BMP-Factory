import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { renderWithProviders } from '../test/renderWithProviders';
import { useAuthStore } from '../stores/authStore';
import { AppLayout } from './AppLayout';

// Phase 5.5.2 §21: Administration is only shown in the nav to users the JWT roles claim says are
// Administrators — hiding it is UX only, AdminRoute (and every backend endpoint) is the real gate.
describe('AppLayout navigation', () => {
  it('shows the Administration nav item for an Administrator', () => {
    useAuthStore.setState({ accessToken: 'token', user: { userId: 'admin-1', displayName: 'Admin', roles: ['Administrator'] } });
    renderWithProviders(<AppLayout />);

    expect(screen.getByText('Administration')).toBeInTheDocument();
  });

  it('hides the Administration nav item for a non-administrator', () => {
    useAuthStore.setState({ accessToken: 'token', user: { userId: 'user-1', displayName: 'User', roles: [] } });
    renderWithProviders(<AppLayout />);

    expect(screen.queryByText('Administration')).not.toBeInTheDocument();
  });

  it('always shows the other nav items regardless of role', () => {
    useAuthStore.setState({ accessToken: 'token', user: { userId: 'user-1', displayName: 'User', roles: [] } });
    renderWithProviders(<AppLayout />);

    expect(screen.getByText('Process Definitions')).toBeInTheDocument();
    expect(screen.getByText('My Tasks')).toBeInTheDocument();
  });

  // Phase 13 (Part 十六) — the language switch lives in the header and updates the current page's
  // translated text immediately, without a reload or re-login.
  it('switches the nav labels to Traditional Chinese via the header language switch, without reloading', async () => {
    const user = userEvent.setup();
    useAuthStore.setState({ accessToken: 'token', user: { userId: 'user-1', displayName: 'User', roles: [] } });
    renderWithProviders(<AppLayout />, { language: 'en-US' });

    expect(screen.getByText('My Tasks')).toBeInTheDocument();

    await user.click(screen.getByText('English'));
    await user.click(await screen.findByText('繁體中文'));

    expect(await screen.findByText('我的任務')).toBeInTheDocument();
    expect(screen.queryByText('My Tasks')).not.toBeInTheDocument();
  });
});
