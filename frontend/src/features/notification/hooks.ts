import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { getUnreadCount, listNotifications, markAllNotificationsRead, markNotificationRead } from '../../services/notificationService';
import type { NotificationQuery } from '../../types/notification';

// Phase 6.1 — Notification Foundation. Polling via React Query's refetchInterval (Part Y: no
// WebSocket/SignalR/Redis this phase) keeps the unread badge reasonably fresh without new
// infrastructure.
export const notificationKeys = {
  all: ['notifications'] as const,
  list: (query: NotificationQuery) => [...notificationKeys.all, 'list', query] as const,
  unreadCount: ['notifications', 'unread-count'] as const,
};

export function useNotifications(query: NotificationQuery) {
  return useQuery({
    queryKey: notificationKeys.list(query),
    queryFn: () => listNotifications(query),
    placeholderData: (previous) => previous,
  });
}

export function useUnreadCount() {
  return useQuery({
    queryKey: notificationKeys.unreadCount,
    queryFn: getUnreadCount,
    refetchInterval: 30000,
  });
}

export function useMarkNotificationRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => markNotificationRead(id),
    onSuccess: () => {
      // Scoped to this feature's own query family, not the whole app cache (Part P).
      queryClient.invalidateQueries({ queryKey: notificationKeys.all });
    },
  });
}

export function useMarkAllNotificationsRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => markAllNotificationsRead(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: notificationKeys.all });
    },
  });
}
