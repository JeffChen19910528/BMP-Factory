namespace BPM.Domain.Entities;

// Phase 6.2 — Notification Delivery. Deliberately a separate entity from Notification, not an
// extra column bag on it (per this phase's own instruction): Notification answers "who should
// see this, and what should they see" (Phase 6.1, unchanged); NotificationDelivery answers "how
// was this actually delivered on one specific channel, and what is that attempt's state" — a
// 1:N relationship (one Notification can eventually have a row per channel; today, only Email).
// Plain POCO like Notification/AuditLog, not AuditableEntity — see Notification.cs's own comment
// on why; the same reasoning applies here (system-generated, no CreatedBy/UpdatedBy concept).
public class NotificationDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NotificationId { get; set; }

    public DeliveryChannel Channel { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    // Resolved and recorded by the worker at send time (Notification.RecipientUserId -> User.Email
    // — never supplied by the engine or the frontend), not at creation time: the engine that
    // creates the paired Notification/NotificationDelivery row never looks at User.Email at all,
    // keeping Workflow/Approval fully ignorant of the email channel (Part B/F). Recorded here
    // purely for operational diagnosis ("what address did we actually attempt"), not as the
    // authoritative recipient — RecipientUserId (via Notification) always is.
    public string? RecipientAddress { get; set; }

    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }

    // Null once Status is Sent or permanently Failed (nothing left to schedule); set to "now" at
    // creation so a freshly created Pending row is immediately eligible for the next worker poll.
    public DateTime? NextAttemptAt { get; set; } = DateTime.UtcNow;

    // Bounded/sanitized (see NotificationDeliveryProcessor) — never a raw exception dump or
    // anything that could carry SMTP credentials.
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

// InApp is listed for forward-extensibility (Part D: "the architecture should allow future
// channels without modifying Workflow/Approval code") but no InApp NotificationDelivery row is
// ever created this phase — the Notification row itself already *is* the in-app delivery
// (visible in the Notification Center the instant it's persisted), so a separate delivery record
// for it would be pure bookkeeping with no state machine to track.
public enum DeliveryChannel
{
    InApp,
    Email,
}

// Pending -> Processing -> Sent (success), or Pending -> Processing -> Pending (transient failure,
// retry scheduled via NextAttemptAt) -> ... -> Failed (permanent, after MaxAttempts). Processing
// is a claim lease, not a terminal state — see NotificationDeliveryProcessor's claim query for how
// a stale Processing row (worker crashed mid-attempt) is recovered rather than lost forever.
public enum DeliveryStatus
{
    Pending,
    Processing,
    Sent,
    Failed,
}
