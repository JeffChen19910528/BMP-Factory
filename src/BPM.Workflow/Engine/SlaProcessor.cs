using BPM.Application.Common;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BPM.Workflow.Engine;

// Phase 6.4 — the testable core of the SLA Scheduler (Part D/AI: "SlaSchedulerWorker -> SlaProcessor
// -> evaluate TaskSla," worker stays thin, this class holds all the business logic and is
// deterministic given an explicit `now`). Mirrors NotificationDeliveryProcessor's own shape
// (BPM.EmailDelivery) closely: claim via a conditional ExecuteUpdateAsync, no Thread.Sleep, no
// in-memory idempotency state — PostgreSQL is authoritative (Part J/K).
//
// Lives in BPM.Workflow.Engine (not a separate project, unlike EmailDeliveryWorker's
// BPM.Notification) specifically so it can call the internal WorkflowTransitions
// .AddNotificationWithDelivery directly — Part AQ #6/#9: "reuse NotificationService... do not
// introduce a new event bus" — there must be exactly one place that stages a Notification +
// NotificationDelivery pair, and that is already WorkflowTransitions, internal to this assembly.
//
// Strict separation from EmailDeliveryWorker (Part B) is enforced by what this class does NOT do:
// it never references IEmailSender, never touches NotificationDelivery directly, and never knows
// whether Email is even enabled. It only ever creates a Notification via the existing factory —
// exactly the same boundary Workflow/Approval already respect.
public class SlaProcessor
{
    private readonly BpmDbContext _db;
    private readonly IClock _clock;
    private readonly SlaSchedulerSettings _settings;

    public SlaProcessor(BpmDbContext db, IClock clock, IOptions<SlaSchedulerSettings> settings)
    {
        _db = db;
        _clock = clock;
        _settings = settings.Value;
    }

    // Returns how many TaskSla rows this call actually changed (claimed a Warning, an Overdue
    // transition, and/or an Escalation) — for worker logging/test assertions only, never a
    // control-flow signal. 0 when disabled or nothing is currently eligible.
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            return 0;
        }

        var now = _clock.UtcNow;
        var candidateIds = await FindCandidateIdsAsync(now, cancellationToken);

        var changed = 0;
        foreach (var id in candidateIds)
        {
            if (await ProcessOneAsync(id, now, cancellationToken))
            {
                changed++;
            }
        }

        return changed;
    }

    // Part N: database-filtered, bounded batch — never loads every Active TaskSla into memory.
    // Three independent eligibility reasons in one OR (Warning due, Overdue due, Escalation due) —
    // a row can match more than one reason in the same tick (e.g. a zero-delay escalation policy
    // fires the same tick a task becomes Overdue), handled by ProcessOneAsync trying each
    // transition in priority order rather than needing three separate queries/passes.
    private async Task<List<Guid>> FindCandidateIdsAsync(DateTime now, CancellationToken cancellationToken) =>
        await _db.TaskSlas
            .Where(s =>
                (s.Status == TaskSlaStatus.Active && s.DueAt <= now) ||
                (s.Status == TaskSlaStatus.Active && s.WarningAt <= now && s.WarningNotifiedAt == null) ||
                (s.Status == TaskSlaStatus.Overdue && s.EscalationAt != null && s.EscalationAt <= now && s.EscalatedAt == null))
            .OrderBy(s => s.DueAt)
            .Take(_settings.BatchSize)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

    // Tries, in order: Overdue transition, then Warning notification (skipped if Overdue was just
    // claimed — a task already overdue never also gets a stale "approaching deadline" warning),
    // then Escalation (tried regardless of whether Overdue was just claimed this same call, so a
    // zero-delay escalation policy can fire in the same tick a task becomes Overdue). Each
    // sub-operation is independently claimed via its own conditional UPDATE inside its own
    // transaction (Part K/L) — two SlaProcessor instances racing on the same id each call this
    // independently; at most one wins each claim.
    private async Task<bool> ProcessOneAsync(Guid id, DateTime now, CancellationToken cancellationToken)
    {
        var overdueClaimed = await TryTransitionOverdueAsync(id, now, cancellationToken);

        var warningClaimed = false;
        if (!overdueClaimed)
        {
            warningClaimed = await TryNotifyWarningAsync(id, now, cancellationToken);
        }

        var escalated = await TryEscalateAsync(id, now, cancellationToken);

        return overdueClaimed || warningClaimed || escalated;
    }

    // Part K: the conditional UPDATE is the atomic claim — only the worker whose UPDATE actually
    // affects a row (WHERE Status = Active, re-checked at UPDATE time, not just at SELECT time) may
    // create the Overdue notification, exactly like NotificationDeliveryProcessor's own claiming.
    // Part L: wrapped in one explicit transaction spanning the claim UPDATE and the Notification/
    // NotificationDelivery inserts — if SaveChangesAsync throws, the whole transaction rolls back,
    // Status reverts to Active, and the row is picked up again on the next tick (Part AH recovery).
    private async Task<bool> TryTransitionOverdueAsync(Guid id, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var affected = await _db.TaskSlas
            .Where(s => s.Id == id && s.Status == TaskSlaStatus.Active && s.DueAt <= now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Status, TaskSlaStatus.Overdue)
                .SetProperty(s => s.OverdueAt, now), cancellationToken);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var (task, instance) = await LoadTaskContextAsync(id, cancellationToken);
        if (task is null || instance is null)
        {
            // Should not normally happen (a TaskSla always has a live TaskInstance/ProcessInstance
            // — neither is ever deleted). Commit the Overdue transition anyway (it is still true)
            // but skip notification/escalation setup rather than throw against data that will never
            // resolve.
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var responsible = await ResolveResponsibleParticipantsAsync(task, cancellationToken);

        // Part I: overdue notification goes to the responsible participant(s) AND the process
        // initiator — the initiator has a direct stake in an overdue task blocking their process,
        // unlike a Warning (Part H), which is scoped to the responsible participant only.
        var recipients = new HashSet<Guid>(responsible) { instance.InitiatorId };
        foreach (var userId in recipients)
        {
            WorkflowTransitions.AddNotificationWithDelivery(
                _db, userId, NotificationType.SlaOverdue, "SLA Overdue",
                $"The task \"{task.NodeName}\" has exceeded its SLA deadline.",
                nameof(TaskInstance), task.Id.ToString());
        }

        // Part Q: compute and persist EscalationAt exactly once, here, at the moment of the Overdue
        // transition — never recalculated on a later tick even if the policy changes afterward.
        var escalationPolicy = await _db.EscalationPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.ProcessDefinitionId == instance.ProcessDefinitionId && p.NodeId == task.NodeId, cancellationToken);
        if (escalationPolicy is { Enabled: true })
        {
            var escalationAt = now.AddMinutes(escalationPolicy.DelayMinutes);
            await _db.TaskSlas.Where(s => s.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.EscalationAt, escalationAt), cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // Part E: WarningNotifiedAt is the persistent idempotency gate — "Status = Active" in the WHERE
    // additionally guarantees a task that races straight past Warning into Overdue in the same tick
    // never gets a stale Warning claimed after the fact (TryTransitionOverdueAsync already ran
    // first in ProcessOneAsync; if it won, Status is no longer Active here and this UPDATE affects
    // 0 rows).
    private async Task<bool> TryNotifyWarningAsync(Guid id, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var affected = await _db.TaskSlas
            .Where(s => s.Id == id && s.Status == TaskSlaStatus.Active && s.WarningAt <= now && s.WarningNotifiedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.WarningNotifiedAt, now), cancellationToken);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var (task, _) = await LoadTaskContextAsync(id, cancellationToken);
        if (task is not null)
        {
            // Part H: the responsible participant only — not the process initiator, and not every
            // Administrator (no broadcast).
            var responsible = await ResolveResponsibleParticipantsAsync(task, cancellationToken);
            foreach (var userId in responsible)
            {
                WorkflowTransitions.AddNotificationWithDelivery(
                    _db, userId, NotificationType.SlaWarning, "SLA Warning",
                    $"The task \"{task.NodeName}\" is approaching its SLA deadline.",
                    nameof(TaskInstance), task.Id.ToString());
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // Part R: EscalatedAt is the persistent idempotency gate, exactly mirroring WarningNotifiedAt.
    // "Status = Overdue" additionally guarantees escalation can never fire for a task that was
    // completed before ever becoming Overdue (SlaEngine.CompleteIfActiveAsync would have already
    // moved it to Completed, so this UPDATE's WHERE no longer matches — Part Z: "a completed Task
    // must not become Overdue/escalated because a scheduler runs later").
    private async Task<bool> TryEscalateAsync(Guid id, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var affected = await _db.TaskSlas
            .Where(s => s.Id == id && s.Status == TaskSlaStatus.Overdue && s.EscalationAt != null && s.EscalationAt <= now && s.EscalatedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.EscalatedAt, now), cancellationToken);

        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var (task, instance) = await LoadTaskContextAsync(id, cancellationToken);
        if (task is not null && instance is not null)
        {
            var escalationPolicy = await _db.EscalationPolicies.AsNoTracking()
                .SingleOrDefaultAsync(p => p.ProcessDefinitionId == instance.ProcessDefinitionId && p.NodeId == task.NodeId, cancellationToken);

            // Should always exist (EscalationAt is only ever set from one, above) — defensive
            // no-op rather than throw if it was deleted between Overdue and now.
            if (escalationPolicy is not null)
            {
                var assignment = new WorkflowAssignment(escalationPolicy.TargetType, escalationPolicy.TargetValue);
                var targets = await AssignmentResolver.ResolveAsync(_db, assignment, instance.InitiatorId, cancellationToken);
                foreach (var userId in targets)
                {
                    WorkflowTransitions.AddNotificationWithDelivery(
                        _db, userId, NotificationType.SlaEscalated, "SLA Escalated",
                        $"The task \"{task.NodeName}\" was escalated because its SLA was exceeded.",
                        nameof(TaskInstance), task.Id.ToString());
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<(TaskInstance? Task, ProcessInstance? Instance)> LoadTaskContextAsync(Guid taskSlaId, CancellationToken cancellationToken)
    {
        var taskInstanceId = await _db.TaskSlas.AsNoTracking()
            .Where(s => s.Id == taskSlaId)
            .Select(s => s.TaskInstanceId)
            .SingleOrDefaultAsync(cancellationToken);

        var task = await _db.TaskInstances.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskInstanceId, cancellationToken);
        if (task is null)
        {
            return (null, null);
        }

        var instance = await _db.ProcessInstances.AsNoTracking().SingleOrDefaultAsync(p => p.Id == task.ProcessInstanceId, cancellationToken);
        return (task, instance);
    }

    // Part H: reuses exactly the same "who can act right now" resolution ApprovalRequired
    // notification already uses (Sequential -> only the earliest-Order still-Pending assignment(s);
    // All/AnyOne -> every still-Pending assignment) rather than inventing a second resolver. A
    // plain UserTask resolves via its own AssigneeId/AssigneeRole, identical to how
    // WorkflowTransitions' own TaskAssigned notification resolves recipients.
    private async Task<List<Guid>> ResolveResponsibleParticipantsAsync(TaskInstance task, CancellationToken cancellationToken)
    {
        if (task.AssigneeId is Guid userId)
        {
            return new List<Guid> { userId };
        }

        if (task.AssigneeRole is string role)
        {
            return await _db.UserRoles
                .Where(ur => ur.Role!.Name == role && ur.User!.IsActive)
                .Select(ur => ur.UserId)
                .Distinct()
                .ToListAsync(cancellationToken);
        }

        // Neither is set -> this is an ApprovalTask; the responsible participant(s) come from its
        // ApprovalInstance/ApprovalAssignment rows instead (see ApprovalEngine.EnsureSequentialTurn
        // for the identical "current turn" logic this mirrors).
        var approval = await _db.ApprovalInstances
            .Include(a => a.Assignments)
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.TaskInstanceId == task.Id, cancellationToken);
        if (approval is null)
        {
            return new List<Guid>();
        }

        var pending = approval.Assignments.Where(a => a.Status == ApprovalAssignmentStatus.Pending).ToList();
        if (pending.Count == 0)
        {
            return new List<Guid>();
        }

        var actionable = approval.Policy == ApprovalPolicy.Sequential
            ? pending.Where(a => a.Order == pending.Min(p => p.Order)).ToList()
            : pending;

        return actionable.Select(a => a.UserId).Distinct().ToList();
    }
}
