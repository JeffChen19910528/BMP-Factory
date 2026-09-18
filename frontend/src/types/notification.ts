// Mirrors BPM.Application.Notifications.NotificationDto/NotificationQuery on the backend
// (src/BPM.Application/Notifications/NotificationDtos.cs). Read-only from the frontend's
// perspective — there is no create/update DTO because notifications are only ever produced by
// backend workflow/approval/process events (Phase 6.1 §F: no POST /api/notifications for a
// regular frontend user).
export type NotificationType =
  | 'TaskAssigned'
  | 'ApprovalRequired'
  | 'ApprovalReturned'
  | 'TaskCompleted'
  | 'ApprovalCompleted'
  | 'ProcessCompleted'
  | 'ProcessRejected'
  | 'ProcessReturned';

export interface AppNotification {
  id: string;
  type: NotificationType;
  title: string;
  message: string;
  relatedEntityType: string;
  relatedEntityId: string | null;
  isRead: boolean;
  createdAt: string;
  readAt: string | null;
}

export interface NotificationQuery {
  unreadOnly?: boolean;
  page?: number;
  pageSize?: number;
}
