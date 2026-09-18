using BPM.Application.Common;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Workflow.Engine;

// Shared "move the token forward" logic (Skill.md §15 concepts: ExecuteTransition/CreateTask/
// CompleteProcess). Used by WorkflowEngine (starting a process, completing a plain UserTask) and
// by ApprovalEngine (an approval resolving, or a Return rewinding to a previous node) — the two
// callers that are allowed to change what node a ProcessInstance is sitting on. Keeping this in
// one place is what satisfies Skill.md §27: "Do not bypass WorkflowEngine when moving to the next
// workflow node" — ApprovalEngine calls into this rather than reimplementing transition/creation
// logic itself.
internal static class WorkflowTransitions
{
    // Walks exactly one outgoing transition from `fromNode` (no branching support yet — enforced
    // by the validator at publish time) and either creates the next task or completes the process
    // instance when the target is an End node.
    public static async Task AdvanceFromAsync(BpmDbContext db, WorkflowDefinition graph, WorkflowNodeDefinition fromNode, ProcessInstance instance, Guid actingUserId, CancellationToken cancellationToken)
    {
        var transition = graph.Transitions.SingleOrDefault(t => t.Source == fromNode.Id);
        if (transition is null)
        {
            // Only reachable if a published version's graph is inconsistent with what the
            // validator checked at publish time (e.g. corrupted data) — fail closed rather than
            // leaving the instance stuck with no way to progress.
            throw new ConflictAppException("WORKFLOW_TRANSITION_MISSING", $"Node '{fromNode.Id}' has no outgoing transition.");
        }

        var targetNode = graph.Nodes.Single(n => n.Id == transition.Target);

        db.AuditLogs.Add(NewAuditLog(actingUserId, AuditActions.WorkflowTransition, nameof(ProcessInstance), instance.Id.ToString(), new { transition.Id, From = fromNode.Id, To = targetNode.Id }));

        if (targetNode.Type == WorkflowNodeType.End)
        {
            instance.Status = ProcessInstanceStatus.Completed;
            instance.CompletedAt = DateTime.UtcNow;
            db.AuditLogs.Add(NewAuditLog(actingUserId, AuditActions.ProcessCompleted, nameof(ProcessInstance), instance.Id.ToString()));
            AddNotificationWithDelivery(db, instance.InitiatorId, NotificationType.ProcessCompleted, "Process Completed", "Your process request has been completed.", nameof(ProcessInstance), instance.Id.ToString());
            return;
        }

        await CreateTaskForNodeAsync(db, instance, targetNode, actingUserId, cancellationToken);
    }

    // Creates the TaskInstance for `node` (UserTask or ApprovalTask) — the "unit of work" row is
    // shared by both node types (Skill.md §11 lists them as siblings); ApprovalTask additionally
    // gets an ApprovalInstance + resolved ApprovalAssignment rows via ApprovalEngine. Used both by
    // AdvanceFromAsync (moving forward) and by ApprovalEngine.ReturnAsync (rewinding to a previous
    // node re-creates a task there exactly as if the engine had just transitioned into it).
    public static async Task<TaskInstance> CreateTaskForNodeAsync(BpmDbContext db, ProcessInstance instance, WorkflowNodeDefinition node, Guid actingUserId, CancellationToken cancellationToken)
    {
        var task = await CreateTaskInstanceForNodeAsync(db, instance, node, actingUserId, cancellationToken);

        // Phase 6.3: applies to both UserTask and ApprovalTask uniformly (Part N) — a single call
        // site after either branch has created its TaskInstance, rather than duplicated inside each.
        await SlaEngine.ApplyIfApplicableAsync(db, instance, task, cancellationToken);

        return task;
    }

    private static async Task<TaskInstance> CreateTaskInstanceForNodeAsync(BpmDbContext db, ProcessInstance instance, WorkflowNodeDefinition node, Guid actingUserId, CancellationToken cancellationToken)
    {
        switch (node.Type)
        {
            case WorkflowNodeType.UserTask:
            {
                var task = new TaskInstance
                {
                    ProcessInstanceId = instance.Id,
                    NodeId = node.Id,
                    NodeName = node.Name,
                    Status = TaskInstanceStatus.Pending,
                };
                ApplyUserTaskAssignment(task, node, instance.InitiatorId);
                db.TaskInstances.Add(task);
                db.AuditLogs.Add(NewAuditLog(actingUserId, AuditActions.TaskCreated, nameof(TaskInstance), task.Id.ToString(), new { task.NodeId, task.ProcessInstanceId }));

                // Phase 6.1: TaskAssigned. A direct AssigneeId (User/ProcessInitiator assignment)
                // resolves to exactly one recipient; an AssigneeRole resolves to every active user
                // holding that role right now — the same resolution AssignmentResolver's own Role
                // branch already performs, reused here rather than re-derived.
                if (task.AssigneeId is Guid assigneeId)
                {
                    AddNotificationWithDelivery(db, assigneeId, NotificationType.TaskAssigned, "Task Assigned", $"You have been assigned the task \"{task.NodeName}\".", nameof(TaskInstance), task.Id.ToString());
                }
                else if (task.AssigneeRole is not null)
                {
                    var roleUserIds = await db.UserRoles
                        .Where(ur => ur.Role!.Name == task.AssigneeRole && ur.User!.IsActive)
                        .Select(ur => ur.UserId)
                        .Distinct()
                        .ToListAsync(cancellationToken);
                    foreach (var userId in roleUserIds)
                    {
                        AddNotificationWithDelivery(db, userId, NotificationType.TaskAssigned, "Task Assigned", $"A task \"{task.NodeName}\" is available for your role ({task.AssigneeRole}).", nameof(TaskInstance), task.Id.ToString());
                    }
                }

                // Skill.md Phase 4 §19 / Phase 10 Form Version pinning: a UserTask node may
                // reference a form; the FormInstance is created alongside the task. If this
                // ProcessVersion pinned a specific FormVersionId at publish time (see
                // FormReference's own doc comment), every task ever created for this node — across
                // every ProcessInstance running on this version, and every Return re-entry —
                // resolves that exact version. Only a ProcessVersion published before this pinning
                // existed has a null FormVersionId here, in which case FormEngine falls back to
                // its original live-lookup behavior for that one, older version.
                if (node.Form is not null)
                {
                    await FormEngine.CreateInstanceForTaskAsync(db, instance.Id, task.Id, node.Form.FormDefinitionKey, node.Form.FormVersionId, actingUserId, cancellationToken);
                }

                return task;
            }

            case WorkflowNodeType.ApprovalTask:
                return await ApprovalEngine.CreateApprovalTaskAsync(db, instance, node, actingUserId, cancellationToken);

            default:
                // Blocked at publish time by WorkflowDefinitionValidator's UNSUPPORTED_NODE_TYPE
                // check; defensive fail-closed guard in case a version predates that check.
                throw new ConflictAppException("UNSUPPORTED_NODE_TYPE", $"Node '{node.Id}' has type '{node.Type}', which the engine cannot execute.");
        }
    }

    private static void ApplyUserTaskAssignment(TaskInstance task, WorkflowNodeDefinition node, Guid processInitiatorId)
    {
        var assignment = node.Assignment
            ?? throw new ConflictAppException("MISSING_ASSIGNMENT", $"Node '{node.Id}' has no assignment configured.");

        switch (assignment.Type)
        {
            case WorkflowAssignmentType.User:
                task.AssigneeId = Guid.Parse(assignment.Value);
                break;
            case WorkflowAssignmentType.ProcessInitiator:
                task.AssigneeId = processInitiatorId;
                break;
            case WorkflowAssignmentType.Role:
                task.AssigneeRole = assignment.Value;
                break;
            default:
                // UserTask only supports User/Role (see WorkflowDefinitionValidator's
                // SupportedUserTaskAssignmentTypes) — anything else is blocked at publish time.
                throw new ConflictAppException("UNSUPPORTED_ASSIGNMENT_TYPE", $"Assignment type '{assignment.Type}' is not supported for UserTask.");
        }
    }

    // Phase 6.1 — mirrors NewAuditLog's own pattern exactly: a plain entity constructed inline and
    // handed to the caller to `db.Notifications.Add(...)`, never saved here. Every call site sits
    // inside a WorkflowEngine/ApprovalEngine method that ends in exactly one SaveChangesAsync (see
    // both classes' own doc comments), so a Notification added this way is committed atomically
    // with the business mutation that produced it — if that method throws before reaching
    // SaveChangesAsync, the Notification is never persisted either (Part H's transaction-boundary
    // requirement, satisfied for free by the existing "one SaveChanges per operation" pattern
    // rather than by adding a new event-bus/outbox mechanism).
    public static Notification NewNotification(Guid recipientUserId, NotificationType type, string title, string message, string relatedEntityType, string? relatedEntityId) =>
        new()
        {
            RecipientUserId = recipientUserId,
            Type = type,
            Title = title,
            Message = message,
            RelatedEntityType = relatedEntityType,
            RelatedEntityId = relatedEntityId,
        };

    // Phase 6.2 — the one call site every trigger point now uses instead of calling
    // `db.Notifications.Add(NewNotification(...))` directly. Always stages a paired, Pending
    // Email NotificationDelivery row alongside the Notification, committed by the same single
    // SaveChangesAsync as everything else in Part H's transaction-boundary requirement.
    //
    // Deliberately unconditional — this method has no idea whether Email delivery is actually
    // enabled, and never checks EmailSettings (that would mean the Workflow/Approval layer
    // reaching into email configuration, exactly what Part B forbids: "Workflow / Approval must
    // NOT directly send email" — knowing *whether* email is on is already too much coupling).
    // The cost of an always-created Pending row is negligible, and the decision of whether to
    // ever act on it belongs entirely to NotificationDeliveryProcessor/EmailDeliveryWorker
    // (BPM.Notification), which reads EmailSettings.Enabled at claim time and simply never claims
    // Email-channel rows when it's off — the row sits harmlessly Pending forever, exactly as
    // Part AB requires ("worker does not crash when Email is disabled... In-App Notification must
    // continue working").
    public static void AddNotificationWithDelivery(BpmDbContext db, Guid recipientUserId, NotificationType type, string title, string message, string relatedEntityType, string? relatedEntityId)
    {
        var notification = NewNotification(recipientUserId, type, title, message, relatedEntityType, relatedEntityId);
        db.Notifications.Add(notification);
        db.NotificationDeliveries.Add(new NotificationDelivery { NotificationId = notification.Id, Channel = DeliveryChannel.Email, Status = DeliveryStatus.Pending });
    }

    public static AuditLog NewAuditLog(Guid userId, string action, string entityType, string entityId, object? newValue = null, object? oldValue = null) =>
        new()
        {
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValue = oldValue is null ? null : System.Text.Json.JsonSerializer.Serialize(oldValue),
            NewValue = newValue is null ? null : System.Text.Json.JsonSerializer.Serialize(newValue),
        };
}
