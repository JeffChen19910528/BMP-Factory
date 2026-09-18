using BPM.Application.Common;
using BPM.Domain.Entities;

namespace BPM.Application.Notifications;

public record NotificationDto(
    Guid Id,
    NotificationType Type,
    string Title,
    string Message,
    string RelatedEntityType,
    string? RelatedEntityId,
    bool IsRead,
    DateTime CreatedAt,
    DateTime? ReadAt);

public record NotificationQuery(bool? UnreadOnly = null, int Page = 1, int PageSize = 20);

// Phase 6.2 — Administrator-only operational diagnosis ("did this user's email actually go out"),
// not a general-purpose delivery management API (Part S explicitly prefers no new delivery API;
// this is the narrow, justified exception — see NotificationsController's own comment). Never
// exposes SMTP credentials or raw provider responses; LastError is already bounded/sanitized by
// NotificationDeliveryProcessor before it's ever persisted.
public record NotificationDeliveryStatusDto(
    Guid DeliveryId,
    string Channel,
    string Status,
    int AttemptCount,
    DateTime? LastAttemptAt,
    DateTime? SentAt,
    DateTime? NextAttemptAt,
    string? LastError);

public interface INotificationService
{
    // currentUserId always comes from the authenticated caller (ICurrentUserService, via the
    // controller) — never accepted as a query parameter, matching every other caller-scoped query
    // in this app (TaskQueryService.GetMyTasksAsync, AuditLogQueryService, etc.). There is no
    // "get notifications for another user" overload, by design — see NotificationService's own
    // comment on why Administrator does not bypass this.
    Task<PagedResult<NotificationDto>> GetForUserAsync(Guid currentUserId, NotificationQuery query, CancellationToken cancellationToken = default);
    Task<int> GetUnreadCountAsync(Guid currentUserId, CancellationToken cancellationToken = default);
    Task MarkReadAsync(Guid notificationId, Guid currentUserId, CancellationToken cancellationToken = default);
    Task MarkAllReadAsync(Guid currentUserId, CancellationToken cancellationToken = default);

    // Administrator-only (enforced by the controller's [Authorize(Roles="Administrator")], not
    // ownership-checked here — an operational diagnostic, not a user-facing notification read).
    Task<IReadOnlyList<NotificationDeliveryStatusDto>> GetDeliveryStatusAsync(Guid notificationId, CancellationToken cancellationToken = default);
}
