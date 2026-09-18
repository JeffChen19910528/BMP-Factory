using BPM.Application.ProcessMonitoring;
using BPM.Application.Sla;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Phase 7.2.2 — Process Instance Detail + Timeline. Read-only: never modifies ProcessInstance/
// TaskInstance/ApprovalAssignment/TaskSla, never touches WorkflowEngine/ApprovalEngine, and parses
// the workflow graph using the existing WorkflowJson/WorkflowDefinition model — no second parser,
// no new workflow state machine.
public class ProcessInstanceDetailQueryService : IProcessInstanceDetailQueryService
{
    private readonly BpmDbContext _db;
    private readonly IProcessInstanceQueryService _processInstanceQueryService;

    public ProcessInstanceDetailQueryService(BpmDbContext db, IProcessInstanceQueryService processInstanceQueryService)
    {
        _db = db;
        _processInstanceQueryService = processInstanceQueryService;
    }

    public async Task<ProcessInstanceDetailDto?> GetDetailAsync(Guid processInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        // Part 2: the entire authorization decision (401 is the controller's job via [Authorize];
        // 404 vs 403 here) is delegated verbatim to the existing, already-tested
        // IProcessInstanceQueryService.GetByIdAsync — null means not found, a thrown
        // ForbiddenAppException propagates as-is, and reaching the code below at all means the
        // caller is already proven authorized for this exact ProcessInstance. No second
        // authorization implementation exists in this file.
        var processInstance = await _processInstanceQueryService.GetByIdAsync(processInstanceId, currentUserId, currentUserRoles, cancellationToken);
        if (processInstance is null)
        {
            return null;
        }

        var definition = await _db.ProcessDefinitions.AsNoTracking()
            .SingleAsync(pd => pd.Id == processInstance.ProcessDefinitionId, cancellationToken);

        // Part 25/26: the exact ProcessVersion this instance actually started from — never the
        // ProcessDefinition's current/latest version, which may have since changed. Published
        // versions are immutable, so this is safe to read without any snapshot/copy step.
        var version = await _db.ProcessVersions.AsNoTracking()
            .SingleAsync(v => v.Id == processInstance.ProcessVersionId, cancellationToken);
        var graph = WorkflowJson.TryDeserialize(version.DefinitionJson);

        // ---- Bounded, process-scoped queries (never a system-wide table scan) ----

        var taskEntities = await _db.TaskInstances.AsNoTracking()
            .Where(t => t.ProcessInstanceId == processInstanceId)
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);
        var taskIds = taskEntities.Select(t => t.Id).ToList();

        var approvalsByTaskId = await _db.ApprovalInstances.AsNoTracking()
            .Include(a => a.Assignments)
            .Where(a => taskIds.Contains(a.TaskInstanceId))
            .ToDictionaryAsync(a => a.TaskInstanceId, cancellationToken);

        var slasByTaskId = await _db.TaskSlas.AsNoTracking()
            .Where(s => taskIds.Contains(s.TaskInstanceId))
            .ToDictionaryAsync(s => s.TaskInstanceId, cancellationToken);

        var taskDtos = taskEntities
            .Select(t => TaskDtoMapper.BuildDto(t, approvalsByTaskId.GetValueOrDefault(t.Id), slasByTaskId.GetValueOrDefault(t.Id)))
            .ToList();

        // Part 6: strictly sequential engine (confirmed by inspection — see
        // ProcessMonitoringQueryService's own identical finding) means at most one row here today.
        // Never silently pick one if that assumption is ever violated — CurrentTask stays null
        // unless exactly one active task exists.
        var activeTasks = taskDtos.Where(t => t.Status == TaskInstanceStatus.Pending || t.Status == TaskInstanceStatus.InProgress).ToList();
        var currentTask = activeTasks.Count == 1 ? activeTasks[0] : null;

        // Phase 7.2.3 hardening — derived from the raw TaskSla entity (which has
        // WarningNotifiedAt; TaskSlaDto does not), the same computation
        // ProcessMonitoringQueryService uses, so Process Detail's SLA bucket can never disagree
        // with what the Process Monitoring list or Dashboard show for the same task.
        var currentSla = currentTask is not null ? slasByTaskId.GetValueOrDefault(currentTask.Id) : null;
        var slaStatus = currentSla is not null ? ProcessMonitoringSlaStatusMapper.From(currentSla.Status, currentSla.WarningNotifiedAt) : null;

        var initiatorDisplayName = await _db.Users.AsNoTracking()
            .Where(u => u.Id == processInstance.InitiatorId)
            .Select(u => u.DisplayName)
            .SingleOrDefaultAsync(cancellationToken) ?? processInstance.InitiatorId.ToString();

        var workflowProgress = BuildWorkflowProgress(graph, taskEntities);
        var timeline = await BuildTimelineAsync(processInstanceId, taskEntities, approvalsByTaskId, slasByTaskId, cancellationToken);

        return new ProcessInstanceDetailDto(
            ProcessInstanceId: processInstance.Id,
            ProcessDefinitionId: processInstance.ProcessDefinitionId,
            ProcessDefinitionKey: definition.Key,
            ProcessDefinitionName: definition.Name,
            Status: processInstance.Status,
            InitiatorId: processInstance.InitiatorId,
            InitiatorDisplayName: initiatorDisplayName,
            StartedAt: processInstance.StartedAt,
            CompletedAt: processInstance.CompletedAt,
            ActiveTaskCount: activeTasks.Count,
            CurrentTask: currentTask,
            Tasks: taskDtos,
            SlaSummary: currentTask?.Sla,
            SlaStatus: slaStatus,
            WorkflowProgress: workflowProgress,
            Timeline: timeline);
    }

    // ---- Workflow Progress ----

    // Part 14/15: a linear walk, not a generic graph traversal — provably correct today because
    // WorkflowTransitions.AdvanceFromAsync itself only ever follows exactly one outgoing
    // transition per node (enforced by WorkflowDefinitionValidator at publish time; see that
    // method's own doc comment: "no branching support yet"). If a published graph is ever found
    // inconsistent with that (should not happen), the walk simply stops rather than guessing —
    // remaining nodes are never rendered rather than falsely marked.
    private static List<ProcessProgressItemDto> BuildWorkflowProgress(WorkflowDefinition? graph, List<TaskInstance> tasks)
    {
        if (graph is null)
        {
            return new List<ProcessProgressItemDto>();
        }

        var tasksByNodeId = tasks.GroupBy(t => t.NodeId).ToDictionary(g => g.Key, g => g.ToList());

        var startNode = graph.Nodes.FirstOrDefault(n => n.Type == WorkflowNodeType.Start);
        if (startNode is null)
        {
            return new List<ProcessProgressItemDto>();
        }

        var items = new List<ProcessProgressItemDto>();
        var visited = new HashSet<string>();
        WorkflowNodeDefinition? node = startNode;

        while (node is not null && visited.Add(node.Id) && items.Count <= graph.Nodes.Count)
        {
            items.Add(new ProcessProgressItemDto(node.Id, node.Type.ToString(), node.Name, StateFor(node, tasksByNodeId)));

            var transition = graph.Transitions.SingleOrDefault(t => t.Source == node.Id);
            node = transition is null ? null : graph.Nodes.SingleOrDefault(n => n.Id == transition.Target);
        }

        return items;
    }

    private static ProcessProgressState StateFor(WorkflowNodeDefinition node, Dictionary<string, List<TaskInstance>> tasksByNodeId)
    {
        // Start never has its own TaskInstance — it is definitionally already behind the process
        // the moment an instance exists, so it always shows Completed.
        if (node.Type == WorkflowNodeType.Start)
        {
            return ProcessProgressState.Completed;
        }

        if (!tasksByNodeId.TryGetValue(node.Id, out var tasksHere) || tasksHere.Count == 0)
        {
            return ProcessProgressState.Pending;
        }

        // A Return can create a second TaskInstance at a previously-completed node — "Completed"
        // if any of them are no longer active, "Current" if the most recent one still is.
        var hasActive = tasksHere.Any(t => t.Status is TaskInstanceStatus.Pending or TaskInstanceStatus.InProgress);
        return hasActive ? ProcessProgressState.Current : ProcessProgressState.Completed;
    }

    // ---- Timeline ----

    // Part 17/18: a curated, verified-persisted subset of AuditLog actions (confirmed via
    // inspection of every AuditLogs.Add call site in BPM.Workflow.Engine — see
    // PROGRESS.md's Phase 7.2.2 section for the full list found) — deliberately excludes
    // WorkflowTransition (fires on every single transition, pure engine-internal plumbing,
    // redundant with the TaskCompleted/TaskCreated pair that already conveys the same moment more
    // precisely) and every Form* action (form data is out of scope here — Part 27). SLA events
    // are sourced from TaskSla's own persisted WarningNotifiedAt/OverdueAt/EscalatedAt fields
    // (Phase 6.4), never from Notification (a notification's recipient is a separate, private
    // concern from "what happened to this process," and TaskSla's own fields are simply more
    // directly authoritative for "when did this fire").
    private static readonly HashSet<string> MeaningfulActions = new()
    {
        AuditActions.StartProcess,
        AuditActions.TaskCreated,
        AuditActions.TaskCompleted,
        AuditActions.ApprovalApproved,
        AuditActions.ApprovalRejected,
        AuditActions.ApprovalReturned,
        AuditActions.ApprovalDelegated,
        AuditActions.ApprovalTransferred,
        AuditActions.ApproverAdded,
        AuditActions.ApprovalCompleted,
        AuditActions.Reject,
        AuditActions.ProcessCompleted,
    };

    private async Task<List<ProcessTimelineItemDto>> BuildTimelineAsync(
        Guid processInstanceId,
        List<TaskInstance> tasks,
        Dictionary<Guid, ApprovalInstance> approvalsByTaskId,
        Dictionary<Guid, TaskSla> slasByTaskId,
        CancellationToken cancellationToken)
    {
        var taskNodeNameById = tasks.ToDictionary(t => t.Id, t => t.NodeName);
        var approvalInstanceIdToTaskId = approvalsByTaskId.Values.ToDictionary(a => a.Id, a => a.TaskInstanceId);
        var assignmentIdToTaskId = approvalsByTaskId.Values
            .SelectMany(a => a.Assignments.Select(asg => (asg.Id, a.TaskInstanceId)))
            .ToDictionary(x => x.Id, x => x.TaskInstanceId);

        // Bounded to this exact ProcessInstance's own known entity ids (Part 22/35) — never
        // "every AuditLog whose EntityId happens to match," which would be unauthorized/unbounded
        // for the ProcessInstance-scoped rows and coincidental for the others; only rows whose
        // EntityType/EntityId genuinely trace back to this process (via ProcessInstanceId itself,
        // or one of its own already-loaded TaskInstance/ApprovalAssignment/ApprovalInstance ids)
        // are ever candidates.
        var candidateIds = new HashSet<string> { processInstanceId.ToString() };
        foreach (var id in taskNodeNameById.Keys)
        {
            candidateIds.Add(id.ToString());
        }
        foreach (var id in approvalInstanceIdToTaskId.Keys)
        {
            candidateIds.Add(id.ToString());
        }
        foreach (var id in assignmentIdToTaskId.Keys)
        {
            candidateIds.Add(id.ToString());
        }

        var auditRows = await _db.AuditLogs.AsNoTracking()
            .Where(a => candidateIds.Contains(a.EntityId!) && MeaningfulActions.Contains(a.Action))
            .OrderByDescending(a => a.Timestamp)
            .Take(200)
            .Select(a => new { a.Timestamp, a.Action, a.EntityType, a.EntityId, a.UserId })
            .ToListAsync(cancellationToken);

        var actorIds = auditRows.Where(a => a.UserId is not null).Select(a => a.UserId!.Value).Distinct().ToList();
        var actorNames = await _db.Users.AsNoTracking()
            .Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        var items = new List<ProcessTimelineItemDto>();
        foreach (var row in auditRows)
        {
            var parsed = TryResolveTitle(row.Action, row.EntityType, row.EntityId, taskNodeNameById, approvalInstanceIdToTaskId, assignmentIdToTaskId);
            if (parsed is null)
            {
                continue;
            }

            var (sourceId, title) = parsed.Value;
            string? actorName = row.UserId is Guid uid ? actorNames.GetValueOrDefault(uid, uid.ToString()) : null;
            items.Add(new ProcessTimelineItemDto(row.Timestamp, row.Action, title, null, sourceId, row.UserId, actorName));
        }

        // SLA events: sourced directly from each task's own TaskSla row, never recalculated.
        foreach (var sla in slasByTaskId.Values)
        {
            var nodeName = taskNodeNameById.GetValueOrDefault(sla.TaskInstanceId, "task");
            if (sla.WarningNotifiedAt is DateTime warningAt)
            {
                items.Add(new ProcessTimelineItemDto(warningAt, "SlaWarning", $"SLA warning: {nodeName}", null, sla.Id, null, null));
            }
            if (sla.OverdueAt is DateTime overdueAt)
            {
                items.Add(new ProcessTimelineItemDto(overdueAt, "SlaOverdue", $"SLA overdue: {nodeName}", null, sla.Id, null, null));
            }
            if (sla.EscalatedAt is DateTime escalatedAt)
            {
                items.Add(new ProcessTimelineItemDto(escalatedAt, "SlaEscalated", $"SLA escalated: {nodeName}", null, sla.Id, null, null));
            }
        }

        // Part 20: newest first, deterministic tiebreak (Timestamp -> EventType -> SourceId) —
        // never relies on incidental database/enumeration order.
        return items
            .OrderByDescending(i => i.Timestamp)
            .ThenBy(i => i.EventType, StringComparer.Ordinal)
            .ThenBy(i => i.SourceId)
            .Take(200)
            .ToList();
    }

    private static (Guid? SourceId, string Title)? TryResolveTitle(
        string action,
        string entityType,
        string? entityId,
        Dictionary<Guid, string> taskNodeNameById,
        Dictionary<Guid, Guid> approvalInstanceIdToTaskId,
        Dictionary<Guid, Guid> assignmentIdToTaskId)
    {
        if (entityId is null || !Guid.TryParse(entityId, out var id))
        {
            return null;
        }

        string? nodeName = entityType switch
        {
            nameof(TaskInstance) => taskNodeNameById.GetValueOrDefault(id),
            nameof(ApprovalInstance) => approvalInstanceIdToTaskId.TryGetValue(id, out var tid1) ? taskNodeNameById.GetValueOrDefault(tid1) : null,
            nameof(ApprovalAssignment) => assignmentIdToTaskId.TryGetValue(id, out var tid2) ? taskNodeNameById.GetValueOrDefault(tid2) : null,
            nameof(ProcessInstance) => null, // process-level events need no node name suffix.
            _ => null,
        };

        var title = action switch
        {
            AuditActions.StartProcess => "Process started",
            AuditActions.TaskCreated => nodeName is null ? "Task started" : $"Task started: {nodeName}",
            AuditActions.TaskCompleted => nodeName is null ? "Task completed" : $"Task completed: {nodeName}",
            AuditActions.ApprovalApproved => nodeName is null ? "Approved" : $"Approved: {nodeName}",
            AuditActions.ApprovalRejected => nodeName is null ? "Rejected" : $"Rejected: {nodeName}",
            AuditActions.ApprovalReturned => nodeName is null ? "Returned" : $"Returned: {nodeName}",
            AuditActions.ApprovalDelegated => nodeName is null ? "Delegated" : $"Delegated: {nodeName}",
            AuditActions.ApprovalTransferred => nodeName is null ? "Transferred" : $"Transferred: {nodeName}",
            AuditActions.ApproverAdded => nodeName is null ? "Approver added" : $"Approver added: {nodeName}",
            AuditActions.ApprovalCompleted => nodeName is null ? "Approval completed" : $"Approval completed: {nodeName}",
            AuditActions.Reject => "Process rejected",
            AuditActions.ProcessCompleted => "Process completed",
            _ => null,
        };

        return title is null ? null : (id, title);
    }
}
