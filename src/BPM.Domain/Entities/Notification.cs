namespace BPM.Domain.Entities;

// Phase 6.1 — Notification Foundation. Mirrors AuditLog's shape deliberately (plain POCO, no
// AuditableEntity/RowVersion) rather than every other entity's AuditableEntity base: a
// Notification is a system-generated, append-then-mutate-one-flag record like AuditLog is
// append-only, not a user-edited business record needing optimistic concurrency, CreatedBy/
// UpdatedBy, or multi-field versioning. IsRead/ReadAt is the one mutable pair, and "mark read
// twice" is naturally idempotent (see NotificationService.MarkReadAsync) — a real race there
// would at worst produce a slightly different ReadAt, never lost or corrupted data, so no
// RowVersion was added (Phase 5.5.4's own principle: don't add concurrency tokens without a
// demonstrated race).
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; } = Guid.Empty;

    public Guid RecipientUserId { get; set; }
    public NotificationType Type { get; set; }

    // Backend-owned display text (Phase 6.1 §R: "Notification title/message 應該由 backend 決定" —
    // never reconstructed from NotificationType in the frontend) — kept as plain strings rather
    // than a template/placeholder system, since there is no template engine anywhere else in this
    // codebase and Phase 6.2 (Email templates) is explicitly out of scope for this phase.
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    // What this notification is about, for frontend navigation — same loose (string type, string
    // id) shape AuditLog already uses for the same reason (one relation can point at any BPM
    // entity type, not just one specific table).
    public string RelatedEntityType { get; set; } = string.Empty;
    public string? RelatedEntityId { get; set; }

    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt { get; set; }
}

// Phase 6.1 §C: distinct from every existing status enum (TaskInstanceStatus, ApprovalInstanceStatus,
// ProcessInstanceStatus, ApprovalAssignmentStatus) — a notification type is "what happened," not a
// state a workflow entity transitions through, so it deliberately does not reuse or extend any of
// those. Serialized as a string by NotificationConfiguration (see its own comment) so the persisted
// value stays a stable, human-readable name rather than an ordinal that shifts if this list is
// ever reordered.
//
// ApprovalReturned vs. ProcessReturned (a real design decision, not an oversight): both exist
// because ReturnAsync has two genuinely distinct audiences, not because of a naming ambiguity —
// ApprovalReturned notifies the OTHER pending co-approvers on the same ApprovalInstance whose
// assignment gets cancelled by CancelRemainingPending (see ApprovalEngine.ReturnAsync) — "you no
// longer need to act, this was returned" — while ProcessReturned notifies the process Initiator
// specifically — "your request was sent back for revision." Collapsing these into one type would
// have been the actual overlapping-enum mistake, since the recipients and messages are genuinely
// different audiences for the same backend event, not duplicated concepts.
public enum NotificationType
{
    TaskAssigned,
    ApprovalRequired,
    ApprovalReturned,
    TaskCompleted,
    ApprovalCompleted,
    ProcessCompleted,
    ProcessRejected,
    ProcessReturned,

    // Phase 6.4 — SLA Scheduler. Distinct from every task/process-lifecycle type above: these fire
    // on a time-based SLA evaluation (SlaProcessor), never on a workflow action. Stored as a string
    // (NotificationConfiguration), so adding these required no migration.
    SlaWarning,
    SlaOverdue,
    SlaEscalated,
}
