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
// As of Phase 3, ApprovalTask-specific actions (Approve/Reject/Return/Delegate/Transfer/
// AddApprover) delegate to ApprovalEngine, a separate class per Skill.md Phase 3 §5's
// "Approval Engine must not duplicate the Workflow Engine" — but both share this same DbContext
// and both funnel "move to the next node" through WorkflowTransitions, so the atomicity and
// audit-trail guarantees above hold across the whole surface, not just the Phase 2 methods.
//
// Graphs are still strictly sequential (Start -> (UserTask|ApprovalTask)* -> End), enforced at
// publish time by WorkflowDefinitionValidator (every non-End node has exactly one outgoing
// transition) — there is deliberately no gateway/branching evaluation here yet.
public class WorkflowEngine : IWorkflowEngine
{
    private readonly BpmDbContext _db;
    private readonly ApprovalEngine _approvalEngine;

    public WorkflowEngine(BpmDbContext db)
    {
        _db = db;
        _approvalEngine = new ApprovalEngine(db);
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

        // Skill.md Phase 4 §19: a UserTask's form reference names a FormDefinition by key, which
        // WorkflowDefinitionValidator (pure, DB-free) can't check exists — verified here instead,
        // the same "fail closed at publish time" principle, just requiring a DB round trip.
        var formKeys = parsed!.Nodes.Where(n => n.Form is not null).Select(n => n.Form!.FormDefinitionKey).Distinct().ToList();
        if (formKeys.Count > 0)
        {
            var publishedFormKeys = await _db.FormDefinitions
                .Where(f => formKeys.Contains(f.Key) && f.Status == FormDefinitionStatus.Published)
                .Select(f => f.Key)
                .ToListAsync(cancellationToken);

            var missing = formKeys.Except(publishedFormKeys).ToList();
            if (missing.Count > 0)
            {
                throw new ValidationAppException(
                    "WORKFLOW_DEFINITION_INVALID",
                    "Workflow definition references forms that don't exist or aren't published.",
                    missing.Select(key => ("FORM_REFERENCE_INVALID", $"Referenced form '{key}' does not exist or has no published version.")).ToList());
            }
        }

        draft.Status = ProcessVersionStatus.Published;
        draft.PublishedBy = publishedBy;
        draft.PublishedAt = DateTime.UtcNow;

        definition.Status = ProcessDefinitionStatus.Published;
        definition.CurrentVersionId = draft.Id;

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(publishedBy, AuditActions.PublishProcess, nameof(ProcessVersion), draft.Id.ToString(), new { draft.ProcessDefinitionId, draft.VersionNumber }));

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

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(initiatorId, AuditActions.StartProcess, nameof(ProcessInstance), instance.Id.ToString(), new { definition.Key, version.VersionNumber, instance.BusinessKey }));

        await WorkflowTransitions.AdvanceFromAsync(_db, graph, startNode, instance, initiatorId, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return new ProcessInstanceDto(instance.Id, instance.ProcessDefinitionId, instance.ProcessVersionId, instance.BusinessKey, instance.InitiatorId, instance.Status, instance.StartedAt, instance.CompletedAt);
    }

    public async Task<TaskDto> CompleteTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var task = await _db.TaskInstances
            .Include(t => t.ProcessInstance)
            .SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new NotFoundAppException("TASK_NOT_FOUND", $"Task '{taskId}' was not found.");

        // ApprovalTask tasks have their own action set (Skill.md Phase 3 §25) — completing one
        // directly would bypass approval policy entirely.
        var isApprovalTask = await _db.ApprovalInstances.AnyAsync(a => a.TaskInstanceId == taskId, cancellationToken);
        if (isApprovalTask)
        {
            throw new BadRequestAppException("TASK_IS_APPROVAL_TASK", $"Task '{taskId}' is an approval task; use POST /api/tasks/{{id}}/approve (or reject/return) instead.");
        }

        // Idempotency by state check (Skill.md §20 — deliberately not over-engineered with an
        // Idempotency-Key store): a retry of an already-completed task fails here instead of
        // creating a second next-task or double-completing the process.
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

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.TaskCompleted, nameof(TaskInstance), task.Id.ToString(), new { task.NodeId, task.ProcessInstanceId }));

        var version = await _db.ProcessVersions.SingleAsync(v => v.Id == task.ProcessInstance.ProcessVersionId, cancellationToken);
        var graph = WorkflowJson.TryDeserialize(version.DefinitionJson)
            ?? throw new ConflictAppException("PROCESS_VERSION_CORRUPT", "The process version's definition could not be parsed.");

        var currentNode = graph.Nodes.Single(n => n.Id == task.NodeId);
        await WorkflowTransitions.AdvanceFromAsync(_db, graph, currentNode, task.ProcessInstance, currentUserId, cancellationToken);

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

    public Task<TaskDto> ApproveTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default) =>
        _approvalEngine.ApproveAsync(taskId, currentUserId, currentUserRoles, cancellationToken);

    public Task<TaskDto> RejectTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default) =>
        _approvalEngine.RejectAsync(taskId, currentUserId, currentUserRoles, cancellationToken);

    public Task<TaskDto> ReturnTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default) =>
        _approvalEngine.ReturnAsync(taskId, currentUserId, currentUserRoles, cancellationToken);

    public Task<TaskDto> DelegateTaskAsync(Guid taskId, Guid currentUserId, Guid delegateToUserId, CancellationToken cancellationToken = default) =>
        _approvalEngine.DelegateAsync(taskId, currentUserId, delegateToUserId, cancellationToken);

    public Task<TaskDto> TransferTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, Guid newUserId, string? reason, CancellationToken cancellationToken = default) =>
        _approvalEngine.TransferAsync(taskId, currentUserId, currentUserRoles, newUserId, reason, cancellationToken);

    public Task<TaskDto> AddApproverAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, Guid newApproverUserId, CancellationToken cancellationToken = default) =>
        _approvalEngine.AddApproverAsync(taskId, currentUserId, currentUserRoles, newApproverUserId, cancellationToken);

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
}
