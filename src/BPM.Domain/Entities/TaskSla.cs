namespace BPM.Domain.Entities;

// Phase 6.3 — the actual per-task execution record: "this specific TaskInstance's SLA clock
// started at X, warns at Y, is due at Z." Plain POCO like Notification/AuditLog, not
// AuditableEntity — system-calculated and system-completed, never directly user-edited (Part W:
// "Do not introduce unnecessary RowVersion to TaskSLA if it is not directly user-editable"). Its
// timestamps are persisted once at creation and are never recalculated afterward, even if the
// originating SlaPolicy is later edited (Part J/X) — this row *is* the historical record.
//
// Deliberately separate from TaskInstance (Part B) rather than adding Policy/Warning/Status
// fields directly onto it: different processes need different SLA durations for "the same"
// logical step, and TaskInstance must remain purely the workflow execution object. The one
// exception is TaskInstance.DueAt — see WorkflowTransitions' own comment on why that one
// pre-existing, previously-unused column is mirrored here as a read-only convenience for the
// existing TaskDto/ApprovalWorklistItemDto consumers, without TaskInstance ever holding a Policy
// reference, WarningAt, Status, or CompletedAt of its own.
public class TaskSla
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // One TaskInstance has at most one TaskSla (unique index — see TaskSlaConfiguration). A
    // Return creates a brand-new TaskInstance at the previous node (verified in
    // ApprovalEngine.ReturnAsync before relying on this), which goes through the same
    // creation path and gets its own fresh TaskSla — never a second row for the same task.
    public Guid TaskInstanceId { get; set; }

    public Guid PolicyId { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime WarningAt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public TaskSlaStatus Status { get; set; } = TaskSlaStatus.Active;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Phase 6.4 — SLA Scheduler. Persistent idempotency state, not an in-memory flag (Part J/K):
    // set exactly once, by whichever SlaSchedulerWorker instance wins the conditional
    // `ExecuteUpdateAsync` claim (`WHERE WarningNotifiedAt IS NULL AND Status = Active`) — see
    // SlaProcessor. A non-null value means "a SlaWarning notification has already been created for
    // this TaskSla," full stop; the scheduler never re-checks WarningAt once this is set. Never
    // repopulated if the SLA later becomes Overdue or Completed.
    public DateTime? WarningNotifiedAt { get; set; }

    // When this TaskSla actually transitioned Active -> Overdue (distinct from DueAt, which is the
    // threshold that triggered it) — set exactly once, by whichever worker wins the conditional
    // Active -> Overdue claim. Never cleared, even if the task is later completed (Status moves to
    // Completed, but OverdueAt remains as the historical record that this task breached its SLA —
    // see SlaEngine.CompleteIfActiveAsync's own comment on completing an Overdue SLA).
    public DateTime? OverdueAt { get; set; }

    // The calculated moment escalation should fire, computed exactly once at the same time as
    // OverdueAt (Part Q: "persist the calculated escalation timing... do not recalculate every
    // polling cycle") from whichever EscalationPolicy matches this TaskSla's (ProcessDefinitionId,
    // NodeId) at that moment. Null if no matching, enabled EscalationPolicy existed when this SLA
    // became Overdue — escalation for this TaskSla will never fire, even if a policy is added
    // later (the same "policy changes never rewrite an existing row's history" rule as SlaPolicy
    // itself, Part J/X, applied identically to escalation).
    public DateTime? EscalationAt { get; set; }

    // Persistent idempotency state for escalation, exactly mirroring WarningNotifiedAt — set once,
    // by whichever worker wins the conditional claim (`WHERE EscalationAt IS NOT NULL AND
    // EscalatedAt IS NULL AND EscalationAt <= now AND Status = Overdue`).
    public DateTime? EscalatedAt { get; set; }
}

// Phase 6.3 introduced Active -> Completed and (reserved, currently unreachable) Active ->
// Cancelled — mirroring TaskInstanceStatus's own "Cancelled is reserved, no code path sets it yet"
// precedent. Phase 6.4 adds the time-based transition Active -> Overdue (set exclusively by
// SlaProcessor's conditional claim, never by SlaEngine) and widens SlaEngine.CompleteIfActiveAsync
// to also complete from Overdue (a task can be completed *after* its SLA already breached — the
// final state is always Completed, with OverdueAt preserved as history; see SlaEngine's own
// comment on the completion-race semantics this encodes). There is no separate "Warning" status
// member — a warning is represented purely by WarningNotifiedAt being non-null while Status is
// still Active, never a status transition of its own (Part E's own "prefer a field over a new
// status" guidance).
public enum TaskSlaStatus
{
    Active,
    Completed,
    Cancelled,
    Overdue,
}
