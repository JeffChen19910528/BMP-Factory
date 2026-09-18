using BPM.Application.Common;
using BPM.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

// Phase 6.1 — every action is scoped to the authenticated caller via ICurrentUserService; there is
// deliberately no {userId} route segment or userId query parameter anywhere on this controller —
// see NotificationQuery/INotificationService's own comments on why (Part G: a client must never be
// able to request another user's notifications, mark another user's notification read, or specify
// a recipient — this controller has no endpoint that even accepts a recipient at all).
[ApiController]
[Authorize]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly ICurrentUserService _currentUser;

    public NotificationsController(INotificationService notificationService, ICurrentUserService currentUser)
    {
        _notificationService = notificationService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<NotificationDto>>> GetAll([FromQuery] NotificationQuery query, CancellationToken cancellationToken) =>
        Ok(await _notificationService.GetForUserAsync(_currentUser.RequireUserId(), query, cancellationToken));

    [HttpGet("unread-count")]
    public async Task<ActionResult<int>> GetUnreadCount(CancellationToken cancellationToken) =>
        Ok(await _notificationService.GetUnreadCountAsync(_currentUser.RequireUserId(), cancellationToken));

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        await _notificationService.MarkReadAsync(id, _currentUser.RequireUserId(), cancellationToken);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        await _notificationService.MarkAllReadAsync(_currentUser.RequireUserId(), cancellationToken);
        return NoContent();
    }

    // Phase 6.2 — the one narrow, justified exception to "no delivery API" (Part S): an
    // Administrator-only operational diagnostic ("did this notification's email actually go
    // out"), not a general delivery-management endpoint. No SMTP credentials, no raw provider
    // response — see NotificationDeliveryStatusDto's own comment.
    [HttpGet("{id:guid}/delivery")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<IReadOnlyList<NotificationDeliveryStatusDto>>> GetDeliveryStatus(Guid id, CancellationToken cancellationToken) =>
        Ok(await _notificationService.GetDeliveryStatusAsync(id, cancellationToken));
}
