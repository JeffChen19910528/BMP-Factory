import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { AuditLogEntry } from '../../types/audit';
import type { PagedResult } from '../../types/process';
import { AuditLogsPage } from './AuditLogsPage';

const { queryAuditLogs } = vi.hoisted(() => ({ queryAuditLogs: vi.fn() }));
vi.mock('../../services/auditLogService', () => ({ queryAuditLogs }));

function entry(overrides: Partial<AuditLogEntry> = {}): AuditLogEntry {
  return {
    id: 'log-1',
    userId: 'admin-1',
    action: 'CreateUser',
    entityType: 'User',
    entityId: 'user-1',
    oldValue: null,
    newValue: '{"username":"alice"}',
    timestamp: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function paged(items: AuditLogEntry[], totalCount = items.length): PagedResult<AuditLogEntry> {
  return { items, totalCount, page: 1, pageSize: 20 };
}

beforeEach(() => {
  vi.clearAllMocks();
});

// Audit Logs workspace (Phase 5.5.2 §14-18): read-only — these tests cover the list/loading/
// empty/error states and that filter changes re-query the server (no client-side filtering here,
// unlike Users/Departments/Roles — see hooks.ts for why Audit Logs alone uses real pagination).
describe('AuditLogsPage', () => {
  it('shows a loading state, then the list of audit log entries', async () => {
    queryAuditLogs.mockResolvedValue(paged([entry()]));
    renderWithProviders(<AuditLogsPage />);

    await waitFor(() => expect(screen.getByText('CreateUser')).toBeInTheDocument());
    expect(screen.getByText('User')).toBeInTheDocument();
  });

  it('shows an empty state when there are no audit log entries', async () => {
    queryAuditLogs.mockResolvedValue(paged([]));
    renderWithProviders(<AuditLogsPage />);

    await waitFor(() => expect(screen.getByText(/No audit log entries match/i)).toBeInTheDocument());
  });

  it('shows an error state when the query fails', async () => {
    queryAuditLogs.mockRejectedValue({
      isAxiosError: true,
      response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } },
    });
    renderWithProviders(<AuditLogsPage />);

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('shows a 403 error when a non-administrator is rejected', async () => {
    queryAuditLogs.mockRejectedValue({ isAxiosError: true, response: { status: 403 } });
    renderWithProviders(<AuditLogsPage />);

    await waitFor(() => expect(screen.getByText(/don't have permission/i)).toBeInTheDocument());
  });

  it('re-queries with the actor filter when changed', async () => {
    queryAuditLogs.mockResolvedValue(paged([entry()]));
    const userAction = userEvent.setup();
    renderWithProviders(<AuditLogsPage />);

    await waitFor(() => expect(screen.getByText('CreateUser')).toBeInTheDocument());
    await userAction.type(screen.getByPlaceholderText('Filter by Actor Id'), 'admin-1');

    await waitFor(() => expect(queryAuditLogs).toHaveBeenLastCalledWith(expect.objectContaining({ userId: 'admin-1' })));
  });

  it('re-queries with the entity type filter when changed', async () => {
    queryAuditLogs.mockResolvedValue(paged([entry()]));
    const userAction = userEvent.setup();
    renderWithProviders(<AuditLogsPage />);

    await waitFor(() => expect(screen.getByText('CreateUser')).toBeInTheDocument());
    await userAction.type(screen.getByPlaceholderText('Filter by Entity Type'), 'User');

    await waitFor(() => expect(queryAuditLogs).toHaveBeenLastCalledWith(expect.objectContaining({ entityType: 'User' })));
  });

  it('does not render any edit or delete controls (read-only)', async () => {
    queryAuditLogs.mockResolvedValue(paged([entry()]));
    renderWithProviders(<AuditLogsPage />);

    await waitFor(() => expect(screen.getByText('CreateUser')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: /edit/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /delete/i })).not.toBeInTheDocument();
  });

  it('paginates using the server-provided totalCount', async () => {
    queryAuditLogs.mockResolvedValue(paged([entry()], 45));
    renderWithProviders(<AuditLogsPage />);

    await waitFor(() => expect(screen.getByText('CreateUser')).toBeInTheDocument());
    expect(screen.getByTitle('3')).toBeInTheDocument();
  });
});
