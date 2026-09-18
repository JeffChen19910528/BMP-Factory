import { useState } from 'react';
import { BellOutlined } from '@ant-design/icons';
import { Badge, Button, Empty, List, Popover, Spin, Typography } from 'antd';
import { useNavigate } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { useTranslation } from '../../i18n/LanguageContext';
import type { AppNotification } from '../../types/notification';
import { useMarkAllNotificationsRead, useMarkNotificationRead, useNotifications, useUnreadCount } from './hooks';

// A notification's RelatedEntityType tells us what it's about; only types with an existing page
// get a navigation target (Part O/§R: "不要為了 Notification phase 開始實作 Dashboard / Process
// Monitoring" — ProcessInstance has no detail page yet, so those notifications show but don't
// navigate, rather than link to a page that doesn't exist).
function relatedPath(notification: AppNotification): string | null {
  if (notification.relatedEntityType === 'TaskInstance' && notification.relatedEntityId) {
    return `/tasks/${notification.relatedEntityId}`;
  }
  return null;
}

export function NotificationBell() {
  const [open, setOpen] = useState(false);
  const navigate = useNavigate();
  const { t } = useTranslation();
  const unreadCountQuery = useUnreadCount();
  const notificationsQuery = useNotifications({ page: 1, pageSize: 10 });
  const markReadMutation = useMarkNotificationRead();
  const markAllReadMutation = useMarkAllNotificationsRead();

  function handleClick(notification: AppNotification) {
    if (!notification.isRead) {
      markReadMutation.mutate(notification.id);
    }
    const path = relatedPath(notification);
    if (path) {
      setOpen(false);
      navigate(path);
    }
  }

  const content = (
    <div style={{ width: 360 }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 8 }}>
        <Typography.Text strong>{t('notifications.title')}</Typography.Text>
        <Button
          size="small"
          type="link"
          onClick={() => markAllReadMutation.mutate()}
          loading={markAllReadMutation.isPending}
          disabled={!unreadCountQuery.data}
        >
          {t('common.markAllAsRead')}
        </Button>
      </div>

      {notificationsQuery.isLoading ? (
        <div style={{ textAlign: 'center', padding: '24px 0' }}>
          <Spin />
        </div>
      ) : notificationsQuery.isError ? (
        <ApiErrorAlert error={notificationsQuery.error} title={t('notifications.loadFailed')} />
      ) : notificationsQuery.data?.items.length === 0 ? (
        <Empty description={t('notifications.empty')} style={{ padding: '24px 0' }} />
      ) : (
        <List<AppNotification>
          dataSource={notificationsQuery.data?.items ?? []}
          style={{ maxHeight: 400, overflowY: 'auto' }}
          renderItem={(notification) => (
            <List.Item
              onClick={() => handleClick(notification)}
              style={{
                cursor: 'pointer',
                background: notification.isRead ? 'transparent' : '#e6f4ff',
                paddingInline: 8,
                borderRadius: 4,
              }}
            >
              <List.Item.Meta
                title={
                  <Typography.Text strong={!notification.isRead}>{notification.title}</Typography.Text>
                }
                description={
                  <>
                    <div>{notification.message}</div>
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {new Date(notification.createdAt).toLocaleString()}
                    </Typography.Text>
                  </>
                }
              />
            </List.Item>
          )}
        />
      )}
    </div>
  );

  return (
    <Popover content={content} trigger="click" open={open} onOpenChange={setOpen} placement="bottomRight">
      <Badge count={unreadCountQuery.data ?? 0} size="small">
        <Button type="text" icon={<BellOutlined style={{ fontSize: 18 }} />} aria-label={t('notifications.title')} />
      </Badge>
    </Popover>
  );
}
