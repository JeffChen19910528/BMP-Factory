import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { AdministrationLayout } from './AdministrationLayout';
import { LanguageProvider } from '../../i18n/LanguageContext';

function renderAt(route: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <LanguageProvider initialLanguage="en-US">
      <MemoryRouter initialEntries={[route]}>
        <Routes>
          <Route path="/administration" element={<AdministrationLayout />}>
            <Route path="users" element={<div>Users Content</div>} />
            <Route path="organizations" element={<div>Organizations Content</div>} />
            <Route path="departments" element={<div>Departments Content</div>} />
            <Route path="roles" element={<div>Roles Content</div>} />
            <Route path="sla-policies" element={<div>SLA Policies Content</div>} />
            <Route path="audit" element={<div>Audit Content</div>} />
            <Route path="operational-health" element={<div>Operational Health Content</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
      </LanguageProvider>
    </QueryClientProvider>,
  );
}

describe('AdministrationLayout navigation', () => {
  it('renders the Users/Organizations/Departments/Roles/SLA Policies/Audit Logs/Operational Health sub-nav tabs', () => {
    renderAt('/administration/users');

    expect(screen.getByRole('tab', { name: 'Users' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Organizations' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Departments' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Roles' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'SLA Policies' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Audit Logs' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Operational Health' })).toBeInTheDocument();
  });

  it('highlights the active tab based on the current route', () => {
    renderAt('/administration/departments');

    expect(screen.getByRole('tab', { name: 'Departments' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'Users' })).toHaveAttribute('aria-selected', 'false');
  });

  it('navigates to another sub-page when its tab is clicked', async () => {
    const userAction = userEvent.setup();
    renderAt('/administration/users');

    expect(screen.getByText('Users Content')).toBeInTheDocument();
    await userAction.click(screen.getByRole('tab', { name: 'Roles' }));

    expect(await screen.findByText('Roles Content')).toBeInTheDocument();
  });
});
