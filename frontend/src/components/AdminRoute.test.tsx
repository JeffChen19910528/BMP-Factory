import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { useAuthStore } from '../stores/authStore';
import { LanguageProvider } from '../i18n/LanguageContext';
import { AdminRoute } from './AdminRoute';

describe('AdminRoute', () => {
  it('renders children for an Administrator', () => {
    useAuthStore.setState({ accessToken: 'token', user: { userId: 'admin-1', displayName: 'Admin', roles: ['Administrator'] } });

    render(
      <LanguageProvider initialLanguage="en-US">
        <AdminRoute>
          <div>Protected Content</div>
        </AdminRoute>
      </LanguageProvider>,
    );

    expect(screen.getByText('Protected Content')).toBeInTheDocument();
  });

  it('shows a 403 result for a non-administrator instead of rendering children', () => {
    useAuthStore.setState({ accessToken: 'token', user: { userId: 'user-1', displayName: 'User', roles: [] } });

    render(
      <LanguageProvider initialLanguage="en-US">
        <AdminRoute>
          <div>Protected Content</div>
        </AdminRoute>
      </LanguageProvider>,
    );

    expect(screen.queryByText('Protected Content')).not.toBeInTheDocument();
    expect(screen.getByText(/don't have permission/i)).toBeInTheDocument();
  });
});
