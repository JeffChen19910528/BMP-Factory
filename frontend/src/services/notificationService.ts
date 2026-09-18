import { apiClient } from './apiClient';
import type { PagedResult } from '../types/process';
import type { AppNotification, NotificationQuery } from '../types/notification';

// Every call is implicitly scoped to the authenticated caller by the backend
// (ICurrentUserService, never a userId param here) — see NotificationsController's own comment.
export async function listNotifications(query: NotificationQuery): Promise<PagedResult<AppNotification>> {
  const response = await apiClient.get<PagedResult<AppNotification>>('/api/notifications', { params: query });
  return response.data;
}

export async function getUnreadCount(): Promise<number> {
  const response = await apiClient.get<number>('/api/notifications/unread-count');
  return response.data;
}

export async function markNotificationRead(id: string): Promise<void> {
  await apiClient.post(`/api/notifications/${id}/read`);
}

export async function markAllNotificationsRead(): Promise<void> {
  await apiClient.post('/api/notifications/read-all');
}
