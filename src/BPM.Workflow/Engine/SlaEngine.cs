using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Workflow.Engine;

// Phase 6.3 — the DB-touching half of SLA Foundation (SlaCalculator is the pure half). Mirrors
// WorkflowTransitions.AddNotificationWithDelivery's own shape exactly: a static helper called
// inline from WorkflowTransitions/WorkflowEngine/ApprovalEngine, adding/updating entities on the
// caller's existing DbContext, never calling SaveChangesAsync itself — committed atomically by
// whichever engine method's own single SaveChangesAsync call already runs (Part K's "Task
// creation and SLA creation must be transactionally consistent" requirement, satisfied for free
// by the same pattern already established for Notification in Phase 6.1/6.2, not a new mechanism).
internal static class SlaEngine
{
    // Called once, right after a TaskInstance is created for any node type (UserTask or
    // ApprovalTask — Part N: "Both should use TaskInstance -> TaskSLA," never two separate SLA
    // implementations). Resolves the policy by (ProcessDefinitionId, NodeId); does nothing if none
    // exists or the one found is disabled (Part K: "If no policy: create Task normally, do not
    // create SLA").
    public static async Task ApplyIfApplicableAsync(BpmDbContext db, ProcessInstance instance, TaskInstance task, CancellationToken cancellationToken)
    {
        var policy = await db.SlaPolicies
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.ProcessDefinitionId == instance.ProcessDefinitionId && p.NodeId == task.NodeId, cancellationToken);

        if (policy is null || !policy.Enabled)
        {
            return;
        }

        var startedAt = DateTime.UtcNow;
        var calculated = SlaCalculator.Calculate(policy.DurationMinutes, policy.WarningOffsetMinutes, startedAt);

        db.TaskSlas.Add(new TaskSla
        {
            TaskInstanceId = task.Id,
            PolicyId = policy.Id,
            StartedAt = calculated.StartedAt,
            WarningAt = calculated.WarningAt,
            DueAt = calculated.DueAt,
            Status = TaskSlaStatus.Active,
        });

        // Phase 6.3 wires up TaskInstance.DueAt — a column reserved since Phase 2 (see
        // TaskInstance.cs's own doc comment) and already plumbed through to TaskDto and
        // ApprovalWorklistItemDto, but never populated by anything until now. This is a read-only
        // denormalized mirror of the authoritative TaskSla.DueAt above, not a second source of
        // truth: TaskInstance never gains a Policy reference, WarningAt, Status, or CompletedAt of
        // its own (Part B). Existing list/worklist queries start showing a real due date for free,
        // with zero DTO/API shape change.
        task.DueAt = calculated.DueAt;
    }

    // Called from every place a TaskInstance reaches a terminal outcome that the assignee actually
    // acted on (WorkflowEngine.CompleteTaskAsync; ApprovalEngine's Approve/Reject outcome and
    // Return) — Part M's principle, applied uniformly: completing, rejecting, or returning a task
    // are all "the assignee acted within the window," so all three mark the SLA Completed, not
    // Cancelled. Cancelled is reserved for a future "task/process administratively terminated
    // without anyone acting" capability that does not exist in this engine yet (see
    // TaskSlaStatus's own doc comment — mirrors TaskInstanceStatus.Cancelled's identical
    // reserved-but-unreachable status). A no-op if the task never had an applicable SLA, and
    // idempotent if somehow called twice (only an Active-or-Overdue row is touched).
    //
    // Phase 6.4 — widened from "only Active" to "Active or Overdue" to resolve the task-completion
    // race (Part X): SlaSchedulerWorker may win the atomic Active -> Overdue claim moments before
    // (or even after, from this method's caller's perspective — both run inside their own single
    // SaveChangesAsync/transaction) the assignee actually completes the task. Completion always
    // wins as the *final* state regardless of ordering — Overdue is a historical fact (preserved
    // via OverdueAt, never cleared), not a state that blocks completion. If this method finds no
    // Active-or-Overdue row (because it already raced to Completed some other way, which cannot
    // currently happen but is defensive), it is a safe no-op, matching every other idempotent call
    // site in this engine.
    public static async Task CompleteIfActiveAsync(BpmDbContext db, Guid taskInstanceId, CancellationToken cancellationToken)
    {
        var sla = await db.TaskSlas.SingleOrDefaultAsync(
            s => s.TaskInstanceId == taskInstanceId && (s.Status == TaskSlaStatus.Active || s.Status == TaskSlaStatus.Overdue),
            cancellationToken);
        if (sla is null)
        {
            return;
        }

        sla.Status = TaskSlaStatus.Completed;
        sla.CompletedAt = DateTime.UtcNow;
    }
}
