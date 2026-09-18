using BPM.Application.Common;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Workflow.Engine;

// Approval-specific runtime logic (Skill.md Phase 3 §5): "determines approvers, determines
// approval policy, tracks approval state, determines approval outcome." Deliberately a separate
// class from WorkflowEngine rather than folding these methods directly into it, per §5's
// separation-of-concerns instruction — but it is NOT a separate transaction boundary or a
// separate persistence mechanism: it shares WorkflowEngine's BpmDbContext instance and, when an
// approval resolves, calls back into WorkflowTransitions (the same code CompleteTaskAsync uses)
// rather than reimplementing "move to the next node" — see WorkflowTransitions' doc comment for
// why that matters.
//
// Design decisions worth flagging (each is a real judgment call the spec left open):
//  - Delegate is additive, not exclusive: after A delegates to B, EITHER A or B may act on the
//    assignment. Skill.md §17 only requires "original user remains recorded," not that they lose
//    access, and additive is the safer default (no one accidentally locks themselves out).
//  - Transfer only ever targets the caller's own assignment (no admin override, no "transfer
//    someone else's slot"): the API takes no "from user" field, and a Task can have several
//    distinct approvers (All/AnyOne), so an admin-initiated transfer would be ambiguous about
//    which assignment to move without one. Revisit if that need arises.
//  - AddApprover is Administrator-only (Skill.md §19 doesn't specify who may call it; least
//    privilege is the safer default for a runtime routing change).
//  - Reject fails the *whole* ApprovalInstance immediately under All/Sequential (one rejection
//    blocks a unanimous-approval gate by definition) but only fails AnyOne once every resolved
//    candidate has rejected (since any single remaining approval could still succeed).
internal class ApprovalEngine
{
    private readonly BpmDbContext _db;

    public ApprovalEngine(BpmDbContext db)
    {
        _db = db;
    }

    // Called by WorkflowTransitions when advancing into an ApprovalTask node (also reused by
    // ReturnAsync to recreate a task at a previous node). Resolves every configured assignment,
    // creates the TaskInstance + ApprovalInstance + one ApprovalAssignment per distinct resolved
    // user (Order = resolution order, gates Sequential — see ApprovalAssignment's doc comment).
    public static async Task<TaskInstance> CreateApprovalTaskAsync(BpmDbContext db, ProcessInstance instance, WorkflowNodeDefinition node, Guid actingUserId, CancellationToken cancellationToken)
    {
        var config = node.Approval
            ?? throw new ConflictAppException("MISSING_APPROVAL_CONFIG", $"Node '{node.Id}' has no approval configuration.");

        var resolvedUserIds = await AssignmentResolver.ResolveManyAsync(db, config.Assignments, instance.InitiatorId, cancellationToken);

        var task = new TaskInstance
        {
            ProcessInstanceId = instance.Id,
            NodeId = node.Id,
            NodeName = node.Name,
            Status = TaskInstanceStatus.Pending,
        };
        db.TaskInstances.Add(task);

        var approval = new ApprovalInstance
        {
            TaskInstanceId = task.Id,
            Policy = config.Policy,
            RequiredCount = config.Policy == ApprovalPolicy.AnyOne ? 1 : resolvedUserIds.Count,
            Status = ApprovalInstanceStatus.Pending,
        };
        db.ApprovalInstances.Add(approval);

        for (var i = 0; i < resolvedUserIds.Count; i++)
        {
            var assignment = new ApprovalAssignment
            {
                ApprovalInstanceId = approval.Id,
                UserId = resolvedUserIds[i],
                Order = i,
                Status = ApprovalAssignmentStatus.Pending,
            };
            db.ApprovalAssignments.Add(assignment);
            db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(actingUserId, AuditActions.ApprovalAssigned, nameof(ApprovalAssignment), assignment.Id.ToString(), new { task.NodeId, assignment.UserId, assignment.Order }));

            // Phase 6.1: ApprovalRequired — only for candidates who can actually act right now.
            // All/AnyOne: every resolved candidate can act immediately. Sequential: only Order 0
            // is actionable yet (EnsureSequentialTurn gates the rest) — notifying a later
            // candidate now would be misleading ("required" when it isn't their turn), so they're
            // notified instead when their turn actually arrives (see ResolveApprovalOutcomeAsync/
            // the per-approve advance below is not needed here since Sequential's next candidate
            // only becomes actionable after a prior Approve, which does not create a new
            // ApprovalAssignment row — it is already visible in their My Tasks/worklist without a
            // fresh notification being strictly required for this foundation phase).
            if (config.Policy != ApprovalPolicy.Sequential || i == 0)
            {
                WorkflowTransitions.AddNotificationWithDelivery(db, assignment.UserId, NotificationType.ApprovalRequired, "Approval Required", $"Your approval is required for \"{task.NodeName}\".", nameof(TaskInstance), task.Id.ToString());
            }
        }

        db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(actingUserId, AuditActions.TaskCreated, nameof(TaskInstance), task.Id.ToString(), new { task.NodeId, task.ProcessInstanceId, approval.Policy }));

        return task;
    }

    public async Task<TaskDto> ApproveAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken)
    {
        var (task, approval) = await LoadActiveApprovalTaskAsync(taskId, cancellationToken);
        var mine = ResolveActingAssignment(approval, currentUserId);
        EnsureAssignmentActionable(mine);
        EnsureSequentialTurn(approval, mine);

        mine.Status = ApprovalAssignmentStatus.Approved;
        mine.CompletedAt = DateTime.UtcNow;
        approval.ApprovedCount++;

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.ApprovalApproved, nameof(ApprovalAssignment), mine.Id.ToString(), new { task.NodeId, mine.UserId }));

        var resolved = approval.Policy == ApprovalPolicy.AnyOne || approval.ApprovedCount >= approval.RequiredCount;
        if (resolved)
        {
            await ResolveApprovalOutcomeAsync(task, approval, ApprovalInstanceStatus.Approved, currentUserId, cancellationToken);
        }

        return await SaveAndReturnAsync(task, approval, taskId, cancellationToken);
    }

    public async Task<TaskDto> RejectAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken)
    {
        var (task, approval) = await LoadActiveApprovalTaskAsync(taskId, cancellationToken);
        await EnsureActionAllowedAsync(task, config => config.AllowReject, "REJECT_NOT_ALLOWED", cancellationToken);

        var mine = ResolveActingAssignment(approval, currentUserId);
        EnsureAssignmentActionable(mine);
        EnsureSequentialTurn(approval, mine);

        mine.Status = ApprovalAssignmentStatus.Rejected;
        mine.CompletedAt = DateTime.UtcNow;
        approval.RejectedCount++;

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.ApprovalRejected, nameof(ApprovalAssignment), mine.Id.ToString(), new { task.NodeId, mine.UserId }));

        // All/Sequential: a single reject blocks a unanimous-approval gate immediately.
        // AnyOne: only once every candidate has rejected does the whole gate fail.
        var wholeInstanceRejected = approval.Policy != ApprovalPolicy.AnyOne
            || !approval.Assignments.Any(a => a.Status == ApprovalAssignmentStatus.Pending);

        if (wholeInstanceRejected)
        {
            await ResolveApprovalOutcomeAsync(task, approval, ApprovalInstanceStatus.Rejected, currentUserId, cancellationToken);
        }

        return await SaveAndReturnAsync(task, approval, taskId, cancellationToken);
    }

    public async Task<TaskDto> ReturnAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken)
    {
        var (task, approval) = await LoadActiveApprovalTaskAsync(taskId, cancellationToken);
        var (graph, node, processInstance) = await LoadGraphContextAsync(task, cancellationToken);
        var config = node.Approval!;

        if (config.ReturnPolicy is not { Enabled: true })
        {
            throw new ConflictAppException("RETURN_NOT_ALLOWED", $"Node '{node.Id}' does not allow returning.");
        }

        var mine = ResolveActingAssignment(approval, currentUserId);
        EnsureAssignmentActionable(mine);
        EnsureSequentialTurn(approval, mine);

        // Only PreviousUserTask is supported (Skill.md §16) — the node immediately upstream via
        // this node's single incoming transition (Phase 2/3 graphs have no merge/join yet).
        var incoming = graph.Transitions.Where(t => t.Target == node.Id).ToList();
        if (incoming.Count != 1)
        {
            throw new ConflictAppException("RETURN_TARGET_INVALID", $"Node '{node.Id}' does not have exactly one previous node to return to.");
        }

        var previousNode = graph.Nodes.Single(n => n.Id == incoming[0].Source);
        if (previousNode.Type is not (WorkflowNodeType.UserTask or WorkflowNodeType.ApprovalTask))
        {
            throw new ConflictAppException("RETURN_TARGET_INVALID", $"Node '{node.Id}' has no previous task to return to.");
        }

        mine.Status = ApprovalAssignmentStatus.Returned;
        mine.CompletedAt = DateTime.UtcNow;
        approval.Status = ApprovalInstanceStatus.Returned;

        // Phase 6.1: ApprovalReturned — every OTHER still-Pending co-approver is about to have
        // their assignment cancelled by CancelRemainingPending below; capture them first so each
        // can be told their action is no longer needed (a genuinely distinct audience from
        // ProcessReturned's Initiator notification below — see Notification.cs's own comment on
        // why these are two types, not one).
        var otherPendingApprovers = approval.Assignments.Where(a => a.Id != mine.Id && a.Status == ApprovalAssignmentStatus.Pending).Select(a => a.UserId).ToList();
        CancelRemainingPending(approval);

        task.Status = TaskInstanceStatus.Returned;
        task.CompletedAt = DateTime.UtcNow;
        // Part M: the approver acted (returned it) within the window — Completed, not Cancelled.
        // The *new* TaskInstance CreateTaskForNodeAsync creates below (at previousNode) goes
        // through the normal creation path and gets its own fresh TaskSla if one applies there.
        await SlaEngine.CompleteIfActiveAsync(_db, task.Id, cancellationToken);

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.ApprovalReturned, nameof(ApprovalAssignment), mine.Id.ToString(), new { task.NodeId, Target = previousNode.Id }));

        foreach (var userId in otherPendingApprovers)
        {
            WorkflowTransitions.AddNotificationWithDelivery(_db, userId, NotificationType.ApprovalReturned, "Approval Returned", $"The approval \"{task.NodeName}\" was returned and no longer needs your action.", nameof(TaskInstance), task.Id.ToString());
        }
        WorkflowTransitions.AddNotificationWithDelivery(_db, processInstance.InitiatorId, NotificationType.ProcessReturned, "Process Returned", $"Your request was returned for revision at \"{task.NodeName}\".", nameof(ProcessInstance), processInstance.Id.ToString());

        // Process stays Running throughout — Return rewinds one step, unlike Reject which
        // terminates the process (Skill.md §14 vs §15).
        await WorkflowTransitions.CreateTaskForNodeAsync(_db, processInstance, previousNode, currentUserId, cancellationToken);

        return await SaveAndReturnAsync(task, approval, taskId, cancellationToken);
    }

    public async Task<TaskDto> DelegateAsync(Guid taskId, Guid currentUserId, Guid delegateToUserId, CancellationToken cancellationToken)
    {
        var (task, approval) = await LoadActiveApprovalTaskAsync(taskId, cancellationToken);
        await EnsureActionAllowedAsync(task, config => config.AllowDelegate, "DELEGATE_NOT_ALLOWED", cancellationToken);

        // Original-owner-only: a delegate cannot re-delegate further (no delegation chains).
        var mine = ResolveActingAssignment(approval, currentUserId, originalOwnerOnly: true);
        EnsureAssignmentActionable(mine);

        if (delegateToUserId == currentUserId)
        {
            throw new BadRequestAppException("INVALID_DELEGATE", "Cannot delegate to yourself.");
        }
        await EnsureActiveUserAsync(delegateToUserId, cancellationToken);

        var previousDelegate = mine.DelegatedToUserId;
        mine.DelegatedToUserId = delegateToUserId;

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(
            currentUserId,
            AuditActions.ApprovalDelegated,
            nameof(ApprovalAssignment),
            mine.Id.ToString(),
            newValue: new { OriginalUserId = mine.UserId, DelegateToUserId = delegateToUserId },
            oldValue: previousDelegate is null ? null : new { PreviousDelegateToUserId = previousDelegate }));

        return await SaveAndReturnAsync(task, approval, taskId, cancellationToken);
    }

    public async Task<TaskDto> TransferAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, Guid newUserId, string? reason, CancellationToken cancellationToken)
    {
        var (task, approval) = await LoadActiveApprovalTaskAsync(taskId, cancellationToken);
        await EnsureActionAllowedAsync(task, config => config.AllowTransfer, "TRANSFER_NOT_ALLOWED", cancellationToken);

        var mine = ResolveActingAssignment(approval, currentUserId, originalOwnerOnly: true);
        EnsureAssignmentActionable(mine);

        if (newUserId == currentUserId)
        {
            throw new BadRequestAppException("INVALID_TRANSFER", "Cannot transfer to yourself.");
        }
        await EnsureActiveUserAsync(newUserId, cancellationToken);

        var previousUserId = mine.UserId;
        mine.UserId = newUserId;
        mine.DelegatedToUserId = null;
        mine.AssignedAt = DateTime.UtcNow;

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(
            currentUserId,
            AuditActions.ApprovalTransferred,
            nameof(ApprovalAssignment),
            mine.Id.ToString(),
            newValue: new { PreviousUserId = previousUserId, NewUserId = newUserId, ActorId = currentUserId, Reason = reason, Timestamp = DateTime.UtcNow }));

        return await SaveAndReturnAsync(task, approval, taskId, cancellationToken);
    }

    public async Task<TaskDto> AddApproverAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, Guid newApproverUserId, CancellationToken cancellationToken)
    {
        if (!currentUserRoles.Contains("Administrator"))
        {
            throw new ForbiddenAppException("ADD_APPROVER_REQUIRES_ADMIN", "Only an Administrator may add an approver to an in-progress approval.");
        }

        var (task, approval) = await LoadActiveApprovalTaskAsync(taskId, cancellationToken);
        await EnsureActionAllowedAsync(task, config => config.AllowAddApprover, "ADD_APPROVER_NOT_ALLOWED", cancellationToken);

        if (approval.Assignments.Any(a => a.UserId == newApproverUserId))
        {
            throw new ConflictAppException("ALREADY_AN_APPROVER", $"User '{newApproverUserId}' is already an approver on this task.");
        }
        await EnsureActiveUserAsync(newApproverUserId, cancellationToken);

        var nextOrder = approval.Assignments.Count == 0 ? 0 : approval.Assignments.Max(a => a.Order) + 1;
        var newAssignment = new ApprovalAssignment
        {
            ApprovalInstanceId = approval.Id,
            UserId = newApproverUserId,
            Order = nextOrder,
            Status = ApprovalAssignmentStatus.Pending,
        };
        _db.ApprovalAssignments.Add(newAssignment);

        // AnyOne already only needs 1 of however many candidates exist — adding a candidate
        // doesn't raise the bar. All/Sequential require every resolved approver, so the new one
        // becomes required too (Skill.md §19's worked example).
        if (approval.Policy != ApprovalPolicy.AnyOne)
        {
            approval.RequiredCount += 1;
        }

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.ApproverAdded, nameof(ApprovalAssignment), newAssignment.Id.ToString(), new { task.NodeId, AddedUserId = newApproverUserId, approval.Policy }));

        return await SaveAndReturnAsync(task, approval, taskId, cancellationToken);
    }

    // Applies the decided outcome (Approved or Rejected only — Return is handled entirely by
    // ReturnAsync, whose shape differs enough not to share this) to the ApprovalInstance and
    // every still-Pending sibling assignment, then either advances the workflow (Approved) or
    // terminates the process (Rejected — Skill.md §14/§17).
    private async Task ResolveApprovalOutcomeAsync(TaskInstance task, ApprovalInstance approval, ApprovalInstanceStatus outcome, Guid actingUserId, CancellationToken cancellationToken)
    {
        approval.Status = outcome;
        CancelRemainingPending(approval);

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(actingUserId, AuditActions.ApprovalCompleted, nameof(ApprovalInstance), approval.Id.ToString(), new { approval.Policy, Outcome = outcome }));

        if (outcome == ApprovalInstanceStatus.Approved)
        {
            task.Status = TaskInstanceStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;
            await SlaEngine.CompleteIfActiveAsync(_db, task.Id, cancellationToken);

            var (graph, node, processInstance) = await LoadGraphContextAsync(task, cancellationToken);

            // Phase 6.1: ApprovalCompleted (Approved outcome only — the Rejected outcome below
            // gets its own ProcessRejected notification instead, since that is the more useful,
            // non-redundant signal for a terminal reject; firing both here would double-notify
            // the Initiator for what is, from their perspective, one event).
            if (processInstance.InitiatorId != actingUserId)
            {
                WorkflowTransitions.AddNotificationWithDelivery(_db, processInstance.InitiatorId, NotificationType.ApprovalCompleted, "Approval Completed", $"The approval \"{task.NodeName}\" on your process has been approved.", nameof(TaskInstance), task.Id.ToString());
            }

            await WorkflowTransitions.AdvanceFromAsync(_db, graph, node, processInstance, actingUserId, cancellationToken);
        }
        else
        {
            task.Status = TaskInstanceStatus.Rejected;
            task.CompletedAt = DateTime.UtcNow;
            // Part M: the approver acted (rejected) within the window — this is a fulfilled SLA,
            // not an abandoned one, so it's Completed, not Cancelled (reserved for a future
            // administrative-termination-without-action case that doesn't exist in this engine).
            await SlaEngine.CompleteIfActiveAsync(_db, task.Id, cancellationToken);

            var processInstance = await _db.ProcessInstances.SingleAsync(p => p.Id == task.ProcessInstanceId, cancellationToken);
            processInstance.Status = ProcessInstanceStatus.Rejected;
            processInstance.CompletedAt = DateTime.UtcNow;
            _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(actingUserId, AuditActions.Reject, nameof(ProcessInstance), processInstance.Id.ToString(), new { task.NodeId }));
            WorkflowTransitions.AddNotificationWithDelivery(_db, processInstance.InitiatorId, NotificationType.ProcessRejected, "Process Rejected", $"Your process request was rejected at \"{task.NodeName}\".", nameof(ProcessInstance), processInstance.Id.ToString());
        }
    }

    private static void CancelRemainingPending(ApprovalInstance approval)
    {
        foreach (var pending in approval.Assignments.Where(a => a.Status == ApprovalAssignmentStatus.Pending))
        {
            pending.Status = ApprovalAssignmentStatus.Cancelled;
            pending.CompletedAt = DateTime.UtcNow;
        }
    }

    private async Task<(WorkflowDefinition Graph, WorkflowNodeDefinition Node, ProcessInstance ProcessInstance)> LoadGraphContextAsync(TaskInstance task, CancellationToken cancellationToken)
    {
        var processInstance = await _db.ProcessInstances.SingleAsync(p => p.Id == task.ProcessInstanceId, cancellationToken);
        var version = await _db.ProcessVersions.SingleAsync(v => v.Id == processInstance.ProcessVersionId, cancellationToken);
        var graph = WorkflowJson.TryDeserialize(version.DefinitionJson)
            ?? throw new ConflictAppException("PROCESS_VERSION_CORRUPT", "The process version's definition could not be parsed.");
        var node = graph.Nodes.Single(n => n.Id == task.NodeId);
        return (graph, node, processInstance);
    }

    private async Task EnsureActionAllowedAsync(TaskInstance task, Func<ApprovalConfig, bool> isAllowed, string errorCode, CancellationToken cancellationToken)
    {
        var (_, node, _) = await LoadGraphContextAsync(task, cancellationToken);
        if (!isAllowed(node.Approval!))
        {
            throw new ConflictAppException(errorCode, $"Node '{node.Id}' does not permit this action.");
        }
    }

    private async Task EnsureActiveUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var exists = await _db.Users.AnyAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (!exists)
        {
            throw new NotFoundAppException("USER_NOT_FOUND", $"User '{userId}' was not found or is inactive.");
        }
    }

    private async Task<(TaskInstance Task, ApprovalInstance Approval)> LoadActiveApprovalTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var task = await _db.TaskInstances
            .Include(t => t.ProcessInstance)
            .SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new NotFoundAppException("TASK_NOT_FOUND", $"Task '{taskId}' was not found.");

        var approval = await _db.ApprovalInstances
            .Include(a => a.Assignments)
            .SingleOrDefaultAsync(a => a.TaskInstanceId == taskId, cancellationToken)
            ?? throw new BadRequestAppException("TASK_NOT_APPROVAL_TASK", $"Task '{taskId}' is not an approval task; use POST /api/tasks/{{id}}/complete instead.");

        if (task.Status != TaskInstanceStatus.Pending && task.Status != TaskInstanceStatus.InProgress)
        {
            throw new ConflictAppException("TASK_ALREADY_COMPLETED", $"Task '{taskId}' is already {task.Status}.");
        }

        if (task.ProcessInstance!.Status != ProcessInstanceStatus.Running)
        {
            throw new ConflictAppException("PROCESS_INSTANCE_NOT_RUNNING", $"Process instance '{task.ProcessInstanceId}' is not running.");
        }

        return (task, approval);
    }

    private static ApprovalAssignment ResolveActingAssignment(ApprovalInstance approval, Guid currentUserId, bool originalOwnerOnly = false)
    {
        // Skill.md §20/§27: authorization is backend-authoritative — a user cannot act on
        // another user's approval slot merely by changing the {id} in the URL (IDOR).
        //
        // ResolveManyAsync dedupes by user id, so at most one assignment can have
        // UserId == currentUserId — but a person can simultaneously be a direct candidate on
        // their own assignment AND the delegate for someone else's on the same
        // ApprovalInstance (e.g. two people who both hold the assigned role, one delegating to
        // the other). Prefer acting as themselves; if they're only a delegate, and delegated to
        // by more than one original owner, act through the earliest-Order one, deterministically.
        var direct = approval.Assignments.SingleOrDefault(a => a.UserId == currentUserId);
        if (direct is not null)
        {
            return direct;
        }

        if (!originalOwnerOnly)
        {
            var viaDelegate = approval.Assignments
                .Where(a => a.DelegatedToUserId == currentUserId)
                .OrderBy(a => a.Order)
                .FirstOrDefault();
            if (viaDelegate is not null)
            {
                return viaDelegate;
            }
        }

        throw new ForbiddenAppException("APPROVAL_NOT_ASSIGNED", "You are not an approver on this task.");
    }

    private static void EnsureAssignmentActionable(ApprovalAssignment mine)
    {
        if (mine.Status != ApprovalAssignmentStatus.Pending)
        {
            throw new ConflictAppException("APPROVAL_ALREADY_ACTED", $"Your approval slot is already {mine.Status}.");
        }
    }

    // Sequential gates on Order: only the earliest-Order still-Pending assignment is actionable,
    // so a later approver cannot act before an earlier one (Skill.md §9's "Finance must not be
    // able to approve early").
    private static void EnsureSequentialTurn(ApprovalInstance approval, ApprovalAssignment mine)
    {
        if (approval.Policy != ApprovalPolicy.Sequential)
        {
            return;
        }

        var pending = approval.Assignments.Where(a => a.Status == ApprovalAssignmentStatus.Pending).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var earliestOrder = pending.Min(a => a.Order);
        if (mine.Order != earliestOrder)
        {
            throw new ConflictAppException("APPROVAL_NOT_ACTIVE", "It is not yet your turn to act on this approval.");
        }
    }

    private async Task<TaskDto> SaveAndReturnAsync(TaskInstance task, ApprovalInstance approval, Guid taskId, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Skill.md §21: two approvers racing (e.g. both sides of an AnyOne gate) — whichever
            // commits second loses because the shared ApprovalInstance row it also had to update
            // (ApprovedCount/Status) had already moved. The loser's own mutation is entirely
            // rolled back (SaveChanges is all-or-nothing), so a client retry against fresh state
            // is safe and will correctly re-apply.
            throw new ConflictAppException("TASK_CONFLICT", $"Task '{taskId}' was modified by another request. Reload and retry.");
        }

        return TaskDtoMapper.BuildDto(task, approval);
    }
}
