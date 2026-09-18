import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { useAuthStore } from '../../stores/authStore';
import type { SlaPolicy } from '../../types/slaPolicy';
import type { PagedResult, ProcessDefinition } from '../../types/process';
import { SlaPoliciesPage } from './SlaPoliciesPage';

const { listSlaPolicies, createSlaPolicy, updateSlaPolicy } = vi.hoisted(() => ({
  listSlaPolicies: vi.fn(),
  createSlaPolicy: vi.fn(),
  updateSlaPolicy: vi.fn(),
}));
const { listProcessDefinitions, listProcessVersions } = vi.hoisted(() => ({
  listProcessDefinitions: vi.fn(),
  listProcessVersions: vi.fn(),
}));

vi.mock('../../services/slaPolicyService', () => ({ listSlaPolicies, createSlaPolicy, updateSlaPolicy }));
vi.mock('../../services/processService', () => ({ listProcessDefinitions, listProcessVersions }));

function policy(overrides: Partial<SlaPolicy> = {}): SlaPolicy {
  return {
    id: 'sla-1',
    processDefinitionId: 'proc-1',
    nodeId: 'approve-node',
    enabled: true,
    durationMinutes: 480,
    warningOffsetMinutes: 60,
    rowVersion: 'AAAAAAAAAAE=',
    ...overrides,
  };
}

function definition(overrides: Partial<ProcessDefinition> = {}): ProcessDefinition {
  return {
    id: 'proc-1',
    key: 'leave-request',
    name: 'Leave Request',
    description: null,
    category: 'HR',
    status: 'Published',
    currentVersionId: null,
    createdAt: '2026-01-01T00:00:00Z',
    createdBy: 'user-1',
    updatedAt: null,
    ownerUserId: null,
    rowVersion: 'AA==',
    ...overrides,
  };
}

function pagedResult(items: ProcessDefinition[]): PagedResult<ProcessDefinition> {
  return { items, totalCount: items.length, page: 1, pageSize: 200 };
}

beforeEach(() => {
  vi.clearAllMocks();
  listProcessDefinitions.mockResolvedValue(pagedResult([definition()]));
  listProcessVersions.mockResolvedValue([]);
  useAuthStore.setState({ accessToken: 'token', user: { userId: 'admin-1', displayName: 'Admin', roles: ['Administrator'] } });
});

describe('SlaPoliciesPage', () => {
  it('shows a loading state, then the list of SLA policies with the resolved process name', async () => {
    listSlaPolicies.mockResolvedValue([policy()]);
    renderWithProviders(<SlaPoliciesPage />);

    await waitFor(() => expect(screen.getByText('Leave Request')).toBeInTheDocument());
    expect(screen.getByText('approve-node')).toBeInTheDocument();
  });

  it('shows an empty state when there are no SLA policies', async () => {
    listSlaPolicies.mockResolvedValue([]);
    renderWithProviders(<SlaPoliciesPage />);

    await waitFor(() => expect(screen.getByText(/No SLA policies yet/i)).toBeInTheDocument());
  });

  it('edits an SLA policy and shows the concurrency-conflict banner on a stale save', async () => {
    listSlaPolicies.mockResolvedValue([policy()]);
    updateSlaPolicy.mockRejectedValue({
      isAxiosError: true,
      response: { status: 409, data: { code: 'SLA_POLICY_CONCURRENCY_CONFLICT', message: 'Stale.', traceId: 't-1' } },
    });
    const userAction = userEvent.setup();
    renderWithProviders(<SlaPoliciesPage />);

    await waitFor(() => expect(screen.getByText('Leave Request')).toBeInTheDocument());
    await userAction.click(screen.getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog');
    await userAction.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText(/modified by another administrator/i)).toBeInTheDocument();
  });
});
