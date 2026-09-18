import { screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import { useAuthStore } from '../../stores/authStore';
import type { OperationalHealthResponse } from '../../types/operationalHealth';
import { OperationalHealthPage } from './OperationalHealthPage';

const { getOperationalHealth } = vi.hoisted(() => ({ getOperationalHealth: vi.fn() }));

vi.mock('../../services/operationalHealthService', () => ({ getOperationalHealth }));

function response(overrides: Partial<OperationalHealthResponse> = {}): OperationalHealthResponse {
  return {
    api: { name: 'API', status: 'Healthy', detail: null },
    database: { name: 'Database', status: 'Healthy', detail: null },
    objectStorage: { name: 'Object Storage', status: 'Healthy', detail: null },
    cache: { name: 'Cache', status: 'NotInstrumented', detail: 'No Redis client is wired into the application yet.' },
    notificationDelivery: { pendingCount: 2, processingCount: 0, failedCount: 0, sentLast24Hours: 10 },
    configuration: {
      email: { enabled: true, provider: 'Fake' },
      slaScheduler: { enabled: true, pollIntervalSeconds: 30, batchSize: 50 },
      dashboard: { dueSoonHours: 24 },
    },
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  useAuthStore.setState({ accessToken: 'token', user: { userId: 'admin-1', displayName: 'Admin', roles: ['Administrator'] } });
});

describe('OperationalHealthPage', () => {
  it('shows a loading state, then the component health cards', async () => {
    getOperationalHealth.mockResolvedValue(response());
    renderWithProviders(<OperationalHealthPage />);

    await waitFor(() => expect(screen.getByText('Database')).toBeInTheDocument());
    expect(screen.getAllByText('Healthy').length).toBeGreaterThan(0);
  });

  it('renders an unavailable/NotInstrumented signal honestly, never as fake-healthy', async () => {
    getOperationalHealth.mockResolvedValue(response());
    renderWithProviders(<OperationalHealthPage />);

    await waitFor(() => expect(screen.getByText('Cache')).toBeInTheDocument());
    expect(screen.getByText('NotInstrumented')).toBeInTheDocument();
    expect(screen.getByText(/No Redis client is wired/i)).toBeInTheDocument();
  });

  it('shows an Unhealthy status when a component is down', async () => {
    getOperationalHealth.mockResolvedValue(response({ objectStorage: { name: 'Object Storage', status: 'Unhealthy', detail: 'Bucket unreachable' } }));
    renderWithProviders(<OperationalHealthPage />);

    await waitFor(() => expect(screen.getByText('Unhealthy')).toBeInTheDocument());
    expect(screen.getByText('Bucket unreachable')).toBeInTheDocument();
  });

  it('shows the notification delivery counts', async () => {
    getOperationalHealth.mockResolvedValue(response({ notificationDelivery: { pendingCount: 3, processingCount: 1, failedCount: 5, sentLast24Hours: 42 } }));
    renderWithProviders(<OperationalHealthPage />);

    await waitFor(() => expect(screen.getByText('42')).toBeInTheDocument());
    expect(screen.getByText('5')).toBeInTheDocument();
  });

  it('shows the read-only configuration diagnostic values', async () => {
    getOperationalHealth.mockResolvedValue(response());
    renderWithProviders(<OperationalHealthPage />);

    await waitFor(() => expect(screen.getByText('Read-only Diagnostic Configuration')).toBeInTheDocument());
    expect(screen.getByText('Fake')).toBeInTheDocument();
    expect(screen.getByText('24h')).toBeInTheDocument();
  });

  it('shows an error state when the request fails', async () => {
    getOperationalHealth.mockRejectedValue({
      isAxiosError: true,
      response: { status: 500, data: { code: 'INTERNAL_ERROR', message: 'Something went wrong.', traceId: 't-1' } },
    });
    renderWithProviders(<OperationalHealthPage />);

    await waitFor(() => expect(screen.getAllByText(/Something went wrong/i).length).toBeGreaterThan(0));
  });
});
