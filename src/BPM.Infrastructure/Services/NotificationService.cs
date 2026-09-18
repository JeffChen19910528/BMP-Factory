using BPM.Application.Common;
using BPM.Application.Notifications;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Phase 6.1 — the read/query side of Notification Foundation (list/unread-count/mark-read). The
// write side (creating a Notification when a workflow/approval/process event happens) lives in
// BPM.Workflow.Engine.WorkflowTransitions.NewNotification, called directly by WorkflowEngine/
// ApprovalEngine and committed by their existing single SaveChangesAsync — the same "engine
// writes AuditLog entries inline via WorkflowTransitions.NewAuditLog, no injected IAuditService"
// pattern this codebase already established, extended to Notification for the exact same
// transactional-atomicity reason (see WorkflowTransitions' own comment on NewNotification).
public class NotificationService : INotificationService
{
    private readonly BpmDbContext _db;

    public NotificationService(BpmDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<NotificationDto>> GetForUserAsync(Guid currentUserId, NotificationQuery query, CancellationToken cancellationToken = default)
    {
        var q = _db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == currentUserId);
        if (query.UnreadOnly == true)
        {
            q = q.Where(n => !n.IsRead);
        }

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 200 ? 20 : query.PageSize;

        // Phase 12 — same overflow guard Phase 7.2.3 established in ProcessMonitoringQueryService.
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

        var totalCount = await q.CountAsync(cancellationToken);
        var items = await q
            .OrderByDescending(n => n.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .Select(n => ToDto(n))
            .ToListAsync(cancellationToken);

        return new PagedResult<NotificationDto>(items, totalCount, page, pageSize);
    }

    public async Task<int> GetUnreadCountAsync(Guid currentUserId, CancellationToken cancellationToken = default) =>
        await _db.Notifications.CountAsync(n => n.RecipientUserId == currentUserId && !n.IsRead, cancellationToken);

    public async Task MarkReadAsync(Guid notificationId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var notification = await _db.Notifications.SingleOrDefaultAsync(n => n.Id == notificationId, cancellationToken)
            ?? throw new NotFoundAppException("NOTIFICATION_NOT_FOUND", $"Notification '{notificationId}' was not found.");

        // Ownership check (§G): ANY authenticated user, including an Administrator — a
        // notification is inherently private to its recipient, unlike Users/Departments/Roles/
        // AuditLogs, which are administrative resources by nature. Nothing in the existing
        // architecture defines an "Administrator can read anyone's notifications" capability, and
        // this phase does not invent one (Part G: "不要因為 Administrator 而無條件暴露所有使用者的私人通知").
        if (notification.RecipientUserId != currentUserId)
        {
            throw new ForbiddenAppException("NOTIFICATION_NOT_AUTHORIZED", "You are not authorized to modify this notification.");
        }

        // Idempotent: marking an already-read notification read again is a harmless no-op, not an
        // error — re-stamping ReadAt would be misleading (it would look freshly read), so only the
        // first transition actually writes.
        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task MarkAllReadAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var unread = await _db.Notifications
            .Where(n => n.RecipientUserId == currentUserId && !n.IsRead)
            .ToListAsync(cancellationToken);

        if (unread.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var notification in unread)
        {
            notification.IsRead = true;
            notification.ReadAt = now;
        }
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NotificationDeliveryStatusDto>> GetDeliveryStatusAsync(Guid notificationId, CancellationToken cancellationToken = default) =>
        await _db.NotificationDeliveries
            .AsNoTracking()
            .Where(d => d.NotificationId == notificationId)
            .Select(d => new NotificationDeliveryStatusDto(d.Id, d.Channel.ToString(), d.Status.ToString(), d.AttemptCount, d.LastAttemptAt, d.SentAt, d.NextAttemptAt, d.LastError))
            .ToListAsync(cancellationToken);

    private static NotificationDto ToDto(Notification n) =>
        new(n.Id, n.Type, n.Title, n.Message, n.RelatedEntityType, n.RelatedEntityId, n.IsRead, n.CreatedAt, n.ReadAt);
}
