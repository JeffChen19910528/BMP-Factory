using BPM.Application.Common;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;

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

                // Skill.md Phase 4 §19: a UserTask node may reference a form; the FormInstance is
                // created alongside the task, pinned to whichever FormVersion is currently
                // published (never re-resolved later — see FormInstance's doc comment).
                if (node.Form is not null)
                {
                    await FormEngine.CreateInstanceForTaskAsync(db, instance.Id, task.Id, node.Form.FormDefinitionKey, actingUserId, cancellationToken);
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
