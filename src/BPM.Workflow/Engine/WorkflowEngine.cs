using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Workflow.Validation;
using Microsoft.EntityFrameworkCore;

namespace BPM.Workflow.Engine;

// The only place ProcessInstance/TaskInstance/AuditLog are written together (Skill.md §15,
// §18: Controller -> Application Service -> Workflow Engine -> Domain/Persistence). Every public
// method here does exactly one SaveChangesAsync call after making all of its entity changes, so
// a single DbContext-managed transaction covers the whole operation — there is no window where a
// task is "completed" but the next task or the audit trail is missing (Skill.md §18).
//
// Phase 2 scope: strictly sequential graphs (Start -> UserTask* -> End), enforced at publish time
// by WorkflowDefinitionValidator (every non-End node has exactly one outgoing transition). There
// is deliberately no gateway/branching evaluation here yet.
public class WorkflowEngine : IWorkflowEngine
{
    private readonly BpmDbContext _db;

    public WorkflowEngine(BpmDbContext db)
    {
        _db = db;
    }

    public async Task<ProcessVersionDto> PublishVersionAsync(Guid processDefinitionId, Guid publishedBy, CancellationToken cancellationToken = default)
    {
        var definition = await _db.ProcessDefinitions
            .SingleOrDefaultAsync(p => p.Id == processDefinitionId, cancellationToken)
            ?? throw new NotFoundAppException("PROCESS_DEFINITION_NOT_FOUND", $"Process definition '{processDefinitionId}' was not found.");

        var draft = await _db.ProcessVersions
            .Where(v => v.ProcessDefinitionId == processDefinitionId && v.Status == ProcessVersionStatus.Draft)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundAppException("DRAFT_VERSION_NOT_FOUND", "This process definition has no draft version to publish.");

        var parsed = WorkflowJson.TryDeserialize(draft.DefinitionJson);
        var validation = WorkflowDefinitionValidator.Validate(parsed);
        if (!validation.IsValid)
        {
            throw new ValidationAppException(
                "WORKFLOW_DEFINITION_INVALID",
                "Workflow definition failed validation.",
                validation.Errors.Select(e => (e.Code, e.Message)).ToList());
        }

        draft.Status = ProcessVersionStatus.Published;
        draft.PublishedBy = publishedBy;
        draft.PublishedAt = DateTime.UtcNow;

        definition.Status = ProcessDefinitionStatus.Published;
        definition.CurrentVersionId = draft.Id;

        _db.AuditLogs.Add(NewAuditLog(publishedBy, AuditActions.PublishProcess, nameof(ProcessVersion), draft.Id.ToString(), new { draft.ProcessDefinitionId, draft.VersionNumber }));

        await _db.SaveChangesAsync(cancellationToken);

        return new ProcessVersionDto(draft.Id, draft.ProcessDefinitionId, draft.VersionNumber, draft.Status, parsed!, draft.PublishedAt);
    }

    public async Task<ProcessInstanceDto> StartProcessAsync(StartProcessRequest request, Guid initiatorId, CancellationToken cancellationToken = default)
    {
        var definition = await _db.ProcessDefinitions
            .SingleOrDefaultAsync(p => p.Key == request.ProcessDefinitionKey, cancellationToken)
            ?? throw new NotFoundAppException("PROCESS_DEFINITION_NOT_FOUND", $"Process definition '{request.ProcessDefinitionKey}' was not found.");

        // Skill.md §13: "A process must NOT start from a Draft version."
        if (definition.Status != ProcessDefinitionStatus.Published || definition.CurrentVersionId is null)
        {
            throw new ConflictAppException("PROCESS_DEFINITION_NOT_PUBLISHED", $"Process definition '{request.ProcessDefinitionKey}' has no published version.");
        }

        var version = await _db.ProcessVersions.SingleAsync(v => v.Id == definition.CurrentVersionId, cancellationToken);
        var graph = WorkflowJson.TryDeserialize(version.DefinitionJson)
            ?? throw new ConflictAppException("PROCESS_VERSION_CORRUPT", "The published process version's definition could not be parsed.");

        var startNode = graph.Nodes.Single(n => n.Type == WorkflowNodeType.Start);

        var instance = new ProcessInstance
        {
            ProcessDefinitionId = definition.Id,
            ProcessVersionId = version.Id,
            BusinessKey = request.BusinessKey,
            InitiatorId = initiatorId,
            Status = ProcessInstanceStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        _db.ProcessInstances.Add(instance);

        _db.AuditLogs.Add(NewAuditLog(initiatorId, AuditActions.StartProcess, nameof(ProcessInstance), instance.Id.ToString(), new { definition.Key, version.VersionNumber, instance.BusinessKey }));

        AdvanceFrom(graph, startNode, instance, initiatorId);

        await _db.SaveChangesAsync(cancellationToken);

        return new ProcessInstanceDto(instance.Id, instance.ProcessDefinitionId, instance.ProcessVersionId, instance.BusinessKey, instance.InitiatorId, instance.Status, instance.StartedAt, instance.CompletedAt);
    }

    public async Task<TaskDto> CompleteTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var task = await _db.TaskInstances
            .Include(t => t.ProcessInstance)
            .SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new NotFoundAppException("TASK_NOT_FOUND", $"Task '{taskId}' was not found.");

        // Idempotency by state check (Skill.md §20 — deliberately not over-engineered with an
        // Idempotency-Key store for Phase 2): a retry of an already-completed task fails here
        // instead of creating a second next-task or double-completing the process.
        if (task.Status != TaskInstanceStatus.Pending && task.Status != TaskInstanceStatus.InProgress)
        {
            throw new ConflictAppException("TASK_ALREADY_COMPLETED", $"Task '{taskId}' is already {task.Status}.");
        }

        if (task.ProcessInstance!.Status != ProcessInstanceStatus.Running)
        {
            throw new ConflictAppException("PROCESS_INSTANCE_NOT_RUNNING", $"Process instance '{task.ProcessInstanceId}' is not running.");
        }

        EnsureAuthorized(task, currentUserId, currentUserRoles);

        task.Status = TaskInstanceStatus.Completed;
        task.CompletedAt = DateTime.UtcNow;

        _db.AuditLogs.Add(NewAuditLog(currentUserId, AuditActions.TaskCompleted, nameof(TaskInstance), task.Id.ToString(), new { task.NodeId, task.ProcessInstanceId }));

        var version = await _db.ProcessVersions.SingleAsync(v => v.Id == task.ProcessInstance.ProcessVersionId, cancellationToken);
        var graph = WorkflowJson.TryDeserialize(version.DefinitionJson)
            ?? throw new ConflictAppException("PROCESS_VERSION_CORRUPT", "The process version's definition could not be parsed.");

        var currentNode = graph.Nodes.Single(n => n.Id == task.NodeId);
        AdvanceFrom(graph, currentNode, task.ProcessInstance, currentUserId);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Skill.md §19: two users completing the same task — the loser's UPDATE matched zero
            // rows because RowVersion had already moved. Surface as a conflict, not a crash.
            throw new ConflictAppException("TASK_CONFLICT", $"Task '{taskId}' was modified by another request. Reload and retry.");
        }

        return new TaskDto(task.Id, task.ProcessInstanceId, task.NodeId, task.NodeName, task.AssigneeId, task.AssigneeRole, task.Status, task.CreatedAt, task.StartedAt, task.CompletedAt, task.DueAt);
    }

    // Walks exactly one outgoing transition from `fromNode` (Phase 2 has no branching — enforced
    // by the validator at publish time) and either creates the next TaskInstance or completes the
    // process instance when the target is an End node.
    private void AdvanceFrom(WorkflowDefinition graph, WorkflowNodeDefinition fromNode, ProcessInstance instance, Guid actingUserId)
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

        _db.AuditLogs.Add(NewAuditLog(actingUserId, AuditActions.WorkflowTransition, nameof(ProcessInstance), instance.Id.ToString(), new { transition.Id, From = fromNode.Id, To = targetNode.Id }));

        switch (targetNode.Type)
        {
            case WorkflowNodeType.End:
                instance.Status = ProcessInstanceStatus.Completed;
                instance.CompletedAt = DateTime.UtcNow;
                _db.AuditLogs.Add(NewAuditLog(actingUserId, AuditActions.ProcessCompleted, nameof(ProcessInstance), instance.Id.ToString()));
                break;

            case WorkflowNodeType.UserTask:
                var task = new TaskInstance
                {
                    ProcessInstanceId = instance.Id,
                    NodeId = targetNode.Id,
                    NodeName = targetNode.Name,
                    Status = TaskInstanceStatus.Pending,
                };
                ApplyAssignment(task, targetNode);
                _db.TaskInstances.Add(task);
                _db.AuditLogs.Add(NewAuditLog(actingUserId, AuditActions.TaskCreated, nameof(TaskInstance), task.Id.ToString(), new { task.NodeId, task.ProcessInstanceId }));
                break;

            default:
                // Blocked at publish time by WorkflowDefinitionValidator's UNSUPPORTED_NODE_TYPE
                // check; defensive fail-closed guard in case a version predates that check.
                throw new ConflictAppException("UNSUPPORTED_NODE_TYPE", $"Node '{targetNode.Id}' has type '{targetNode.Type}', which the engine cannot execute.");
        }
    }

    private static void ApplyAssignment(TaskInstance task, WorkflowNodeDefinition node)
    {
        var assignment = node.Assignment
            ?? throw new ConflictAppException("MISSING_ASSIGNMENT", $"Node '{node.Id}' has no assignment configured.");

        switch (assignment.Type)
        {
            case WorkflowAssignmentType.User:
                task.AssigneeId = Guid.Parse(assignment.Value);
                break;
            case WorkflowAssignmentType.Role:
                task.AssigneeRole = assignment.Value;
                break;
            default:
                throw new ConflictAppException("UNSUPPORTED_ASSIGNMENT_TYPE", $"Assignment type '{assignment.Type}' is not yet supported.");
        }
    }

    private static void EnsureAuthorized(TaskInstance task, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles)
    {
        // Skill.md §22/§27: authorization is backend-authoritative — a user cannot complete
        // another user's task merely by changing the {id} in the URL (IDOR).
        if (task.AssigneeId is Guid assigneeId)
        {
            if (assigneeId != currentUserId)
            {
                throw new ForbiddenAppException("TASK_NOT_ASSIGNED_TO_USER", "You are not the assignee of this task.");
            }
            return;
        }

        if (task.AssigneeRole is string role)
        {
            if (!currentUserRoles.Contains(role))
            {
                throw new ForbiddenAppException("TASK_ROLE_NOT_HELD", $"Completing this task requires the '{role}' role.");
            }
            return;
        }

        throw new ConflictAppException("TASK_UNASSIGNED", $"Task '{task.Id}' has no assignee or role configured.");
    }

    private static AuditLog NewAuditLog(Guid userId, string action, string entityType, string entityId, object? newValue = null) =>
        new()
        {
            UserId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            NewValue = newValue is null ? null : System.Text.Json.JsonSerializer.Serialize(newValue),
        };
}
