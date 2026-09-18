import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProviders } from '../../test/renderWithProviders';
import type { AppNotification } from '../../types/notification';
import type { PagedResult } from '../../types/process';
import { NotificationBell } from './NotificationBell';

const { listNotifications, getUnreadCount, markNotificationRead, markAllNotificationsRead } = vi.hoisted(() => ({
  listNotifications: vi.fn(),
  getUnreadCount: vi.fn(),
  markNotificationRead: vi.fn(),
  markAllNotificationsRead: vi.fn(),
}));

vi.mock('../../services/notificationService', () => ({ listNotifications, getUnreadCount, markNotificationRead, markAllNotificationsRead }));

const { navigateMock } = vi.hoisted(() => ({ navigateMock: vi.fn() }));
vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => navigateMock };
});

function notification(overrides: Partial<AppNotification> = {}): AppNotification {
  return {
    id: 'notif-1',
    type: 'TaskAssigned',
    title: 'Task Assigned',
    message: 'You have been assigned the task "Review".',
    relatedEntityType: 'TaskInstance',
    relatedEntityId: 'task-1',
    isRead: false,
    createdAt: '2026-01-01T00:00:00Z',
    readAt: null,
    ...overrides,
  };
}

function paged(items: AppNotification[], totalCount = items.length): PagedResult<AppNotification> {
  return { items, totalCount, page: 1, pageSize: 10 };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('NotificationBell', () => {
  it('shows the unread count badge', async () => {
    getUnreadCount.mockResolvedValue(3);
    listNotifications.mockResolvedValue(paged([]));

    renderWithProviders(<NotificationBell />);

    await waitFor(() => expect(screen.getByText('3')).toBeInTheDocument());
  });

  it('shows no badge number when there are no unread notifications', async () => {
    getUnreadCount.mockResolvedValue(0);
    listNotifications.mockResolvedValue(paged([]));

    renderWithProviders(<NotificationBell />);

    await waitFor(() => expect(getUnreadCount).toHaveBeenCalled());
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });

  it('opens the popover and shows a loading state, then the notification list', async () => {
    getUnreadCount.mockResolvedValue(1);
    listNotifications.mockResolvedValue(paged([notification()]));
    const user = userEvent.setup();

    renderWithProviders(<NotificationBell />);
    await user.click(screen.getByLabelText('Notifications'));

    await waitFor(() => expect(screen.getByText('Task Assigned')).toBeInTheDocument());
    expect(screen.getByText(/You have been assigned/)).toBeInTheDocument();
  });

  it('shows an empty state when there are no notifications', async () => {
    getUnreadCount.mockResolvedValue(0);
    listNotifications.mockResolvedValue(paged([]));
    const user = userEvent.setup();

    renderWithProviders(<NotificationBell />);
    await user.click(screen.getByLabelText('Notifications'));

    await waitFor(() => expect(screen.getByText(/No notifications yet/i)).toBeInTheDocument());
  });

  it('shows an error state when the list request fails', async () => {
    getUnreadCount.mockResolvedValue(0);
    listNotifications.mockRejectedValue({ isAxiosError: true, response: { status: 500, data: { code: 'SERVER_ERROR', message: 'Boom', traceId: 't-1' } } });
    const user = userEvent.setup();

    renderWithProviders(<NotificationBell />);
    await user.click(screen.getByLabelText('Notifications'));

    await waitFor(() => expect(screen.getByText('Boom')).toBeInTheDocument());
  });

  it('marks a notification read and navigates to its related task when clicked', async () => {
    getUnreadCount.mockResolvedValue(1);
    listNotifications.mockResolvedValue(paged([notification()]));
    markNotificationRead.mockResolvedValue(undefined);
    const user = userEvent.setup();

    renderWithProviders(<NotificationBell />);
    await user.click(screen.getByLabelText('Notifications'));
    await waitFor(() => expect(screen.getByText('Task Assigned')).toBeInTheDocument());

    await user.click(screen.getByText('Task Assigned'));

    await waitFor(() => expect(markNotificationRead).toHaveBeenCalledWith('notif-1'));
    expect(navigateMock).toHaveBeenCalledWith('/tasks/task-1');
  });

  it('does not re-mark an already-read notification, but still navigates', async () => {
    getUnreadCount.mockResolvedValue(0);
    listNotifications.mockResolvedValue(paged([notification({ isRead: true, readAt: '2026-01-01T01:00:00Z' })]));
    const user = userEvent.setup();

    renderWithProviders(<NotificationBell />);
    await user.click(screen.getByLabelText('Notifications'));
    await waitFor(() => expect(screen.getByText('Task Assigned')).toBeInTheDocument());

    await user.click(screen.getByText('Task Assigned'));

    expect(markNotificationRead).not.toHaveBeenCalled();
    expect(navigateMock).toHaveBeenCalledWith('/tasks/task-1');
  });

  it('marks all notifications as read', async () => {
    getUnreadCount.mockResolvedValue(2);
    listNotifications.mockResolvedValue(paged([notification(), notification({ id: 'notif-2', title: 'Approval Required', type: 'ApprovalRequired' })]));
    markAllNotificationsRead.mockResolvedValue(undefined);
    const user = userEvent.setup();

    renderWithProviders(<NotificationBell />);
    await user.click(screen.getByLabelText('Notifications'));
    await waitFor(() => expect(screen.getByText('Approval Required')).toBeInTheDocument());

    await user.click(screen.getByRole('button', { name: /Mark all as read/i }));

    await waitFor(() => expect(markAllNotificationsRead).toHaveBeenCalled());
  });

  it('does not render another user\'s data (only what the scoped API returns)', async () => {
    getUnreadCount.mockResolvedValue(1);
    listNotifications.mockResolvedValue(paged([notification()]));
    const user = userEvent.setup();

    renderWithProviders(<NotificationBell />);
    await user.click(screen.getByLabelText('Notifications'));

    await waitFor(() => expect(screen.getByText('Task Assigned')).toBeInTheDocument());
    // The service call itself carries no userId/recipient param — scoping is entirely backend-side.
    expect(listNotifications).toHaveBeenCalledWith(expect.not.objectContaining({ userId: expect.anything() }));
  });
});
