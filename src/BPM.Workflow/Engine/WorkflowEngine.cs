using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Workflow.Validation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    public WorkflowValidationResultDto ValidateDefinition(WorkflowDefinition definition)
    {
        var result = WorkflowDefinitionValidator.Validate(definition);
        return new WorkflowValidationResultDto(
            result.IsValid,
            result.Errors.Select(e => new WorkflowValidationErrorDto(e.Code, e.Message)).ToList());
    }

    public async Task<ProcessVersionDto> PublishVersionAsync(Guid processDefinitionId, Guid publishedBy, string? changeReason = null, CancellationToken cancellationToken = default)
    {
        // Phase 8 — validated here (a plain length check, not FluentValidation) rather than
        // letting Postgres reject an over-length value with a raw 22001 error (which would leak
        // as an unhandled exception — ErrorHandlingMiddleware only catches AppException/
        // ValidationException). Matches ProcessVersionConfiguration's own HasMaxLength(1000).
        if (changeReason is { Length: > 1000 })
        {
            throw new BadRequestAppException("CHANGE_REASON_TOO_LONG", "ChangeReason must not exceed 1000 characters.");
        }

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
            var publishedForms = await _db.FormDefinitions
                .Where(f => formKeys.Contains(f.Key) && f.Status == FormDefinitionStatus.Published)
                .Select(f => new { f.Key, f.CurrentVersionId })
                .ToListAsync(cancellationToken);
            var publishedFormVersionByKey = publishedForms.ToDictionary(f => f.Key, f => f.CurrentVersionId);

            var missing = formKeys.Except(publishedFormVersionByKey.Keys).ToList();
            if (missing.Count > 0)
            {
                throw new ValidationAppException(
                    "WORKFLOW_DEFINITION_INVALID",
                    "Workflow definition references forms that don't exist or aren't published.",
                    missing.Select(key => ("FORM_REFERENCE_INVALID", $"Referenced form '{key}' does not exist or has no published version.")).ToList());
            }

            // Phase 10 — Form Version pinning. Snapshot exactly which FormVersion is current for
            // each referenced form key right now, at the moment this ProcessVersion is published,
            // and freeze that choice into the graph before it's re-serialized below — see
            // FormReference.FormVersionId's own doc comment for why this is the smallest correct
            // fix rather than a new database column.
            var pinnedNodes = parsed.Nodes.Select(n => n.Form is null
                ? n
                : n with { Form = n.Form with { FormVersionId = publishedFormVersionByKey[n.Form.FormDefinitionKey] } }).ToList();
            parsed = parsed with { Nodes = pinnedNodes };
            draft.DefinitionJson = WorkflowJson.Serialize(parsed);
        }

        draft.Status = ProcessVersionStatus.Published;
        draft.PublishedBy = publishedBy;
        draft.PublishedAt = DateTime.UtcNow;
        draft.ChangeReason = changeReason;

        // Phase 8 — publishing a new version while the definition is Suspended/Archived also
        // returns it to Published, matching this phase's own "Suspend/Archive block new starts
        // and freeze metadata, but publishing a fresh version is still a Draft/Publish workflow
        // action, not a governance action" decision; a Draft can only ever be created in the
        // first place from Published/Suspended/Archived (never while Draft already exists — see
        // CreateVersionAsync), so this assignment is a safe, idempotent no-op when already
        // Published and a deliberate un-suspend/un-archive when it wasn't.
        definition.Status = ProcessDefinitionStatus.Published;
        definition.CurrentVersionId = draft.Id;

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(publishedBy, AuditActions.PublishProcess, nameof(ProcessVersion), draft.Id.ToString(), new { draft.ProcessDefinitionId, draft.VersionNumber, draft.ChangeReason }));

        await _db.SaveChangesAsync(cancellationToken);

        return new ProcessVersionDto(draft.Id, draft.ProcessDefinitionId, draft.VersionNumber, draft.Status, parsed!, draft.CreatedAt, draft.CreatedBy, draft.PublishedAt, draft.PublishedBy, draft.ChangeReason, Convert.ToBase64String(draft.RowVersion));
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

        // Phase 10 — Process Start idempotency. A BusinessKey is optional and this check is a
        // no-op fast path when it's null (the unique index below permits unlimited NULLs). When a
        // caller does supply one, a pre-check here would still leave a genuine TOCTOU race between
        // two concurrent Start requests for the same key — the real guard is the database's own
        // unique index on (TenantId, ProcessDefinitionId, BusinessKey)
        // (ProcessInstanceConfiguration.cs), caught below and translated into the same structured
        // {code, message, traceId} contract every other conflict in this codebase already uses,
        // never a raw PostgresException. Whichever of two racing requests loses gets a clean 409
        // and creates no ProcessInstance row at all (the whole method is one SaveChangesAsync, so
        // a caught failure here rolls back everything this call added, including the just-created
        // TaskInstance from AdvanceFromAsync above).
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (request.BusinessKey is not null && IsUniqueViolation(ex))
        {
            throw new ConflictAppException(
                "PROCESS_INSTANCE_DUPLICATE_BUSINESS_KEY",
                $"A process instance with business key '{request.BusinessKey}' already exists for this process.");
        }

        return new ProcessInstanceDto(instance.Id, instance.ProcessDefinitionId, instance.ProcessVersionId, instance.BusinessKey, instance.InitiatorId, instance.Status, instance.StartedAt, instance.CompletedAt);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

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

        // Phase 6.3: the assignee acted within (or after) the window either way — a no-op if this
        // task never had an applicable SLA.
        await SlaEngine.CompleteIfActiveAsync(_db, task.Id, cancellationToken);

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.TaskCompleted, nameof(TaskInstance), task.Id.ToString(), new { task.NodeId, task.ProcessInstanceId }));

        // Phase 6.1: TaskCompleted. Recipient is the process Initiator (kept informed of progress
        // on their own request) — skipped when the Initiator is the one who completed the task
        // themselves (the common case: a UserTask's own applicant step), since notifying someone
        // of their own action is noise, not information.
        if (task.ProcessInstance.InitiatorId != currentUserId)
        {
            WorkflowTransitions.AddNotificationWithDelivery(_db, task.ProcessInstance.InitiatorId, NotificationType.TaskCompleted, "Task Completed", $"The task \"{task.NodeName}\" on your process has been completed.", nameof(TaskInstance), task.Id.ToString());
        }

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
