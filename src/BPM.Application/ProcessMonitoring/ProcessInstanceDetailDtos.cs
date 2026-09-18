using BPM.Application.Sla;
using BPM.Application.Workflow;
using BPM.Domain.Entities;

namespace BPM.Application.ProcessMonitoring;

// Phase 7.2.2 — Process Instance Detail + Timeline. A read model only — this file introduces no
// new entity, no new state machine, and reuses existing DTOs wherever one already fits (`TaskDto`
// for Task History, `TaskSlaDto` for the process-level SLA summary) rather than inventing parallel
// shapes for the same data.

// Node execution state along the process's own linear path (see ProcessInstanceDetailQueryService
// for why a linear walk is provably correct today — the engine enforces exactly one outgoing
// transition per node at publish time, no branching exists yet). Deliberately only these three
// values (Part 15) — no "Skipped"/"Rejected" node state invented; a node the process rejected out
// of still shows Completed (execution passed through it), matching the same "Completed = at least
// one non-active TaskInstance exists here" rule used for Process Monitoring's own progress signal.
public enum ProcessProgressState
{
    Completed,
    Current,
    Pending,
}

public record ProcessProgressItemDto(string NodeId, string NodeType, string DisplayName, ProcessProgressState State);

// Sourced from a curated, verified-persisted subset of AuditLog actions plus TaskSla's own
// WarningNotifiedAt/OverdueAt/EscalatedAt timestamps (Phase 6.4) — never a blind concatenation of
// every AuditLog row touching this process (Part 17), and never Notification (a notification's
// recipient identity is a separate, private concern from "what happened to this process" — see
// the query service's own comment). Title/Description are backend-authored text (the same
// "backend decides display text" principle Notification.Title/Message already established), never
// a raw action constant the frontend has to translate.
public record ProcessTimelineItemDto(
    DateTime Timestamp,
    string EventType,
    string Title,
    string? Description,
    Guid? SourceId,
    Guid? ActorId,
    string? ActorDisplayName);

// Never the ProcessInstance entity itself — no DefinitionJson, no RowVersion, no internal
// workflow/authorization/notification-delivery shape. `Tasks` reuses the existing `TaskDto`
// (already carries each task's own `Approval`/`Sla` nested — Task History, Approval History, and
// per-task SLA are all this one existing, already-tested projection, not three new DTOs).
// `SlaSummary` reuses the existing `TaskSlaDto` (Phase 6.3) directly — it is exactly
// `CurrentTask?.Sla`, never a new aggregation rule or a `ProcessDetailSlaStatus` (Part 13/46: the
// engine is sequential, so "the process's SLA" is simply its one active task's own SLA, with no
// multi-task priority question to answer).
public record ProcessInstanceDetailDto(
    Guid ProcessInstanceId,
    Guid ProcessDefinitionId,
    string ProcessDefinitionKey,
    string ProcessDefinitionName,
    ProcessInstanceStatus Status,
    Guid InitiatorId,
    string InitiatorDisplayName,
    DateTime StartedAt,
    DateTime? CompletedAt,
    // Always 0 or 1 today (see ProcessInstanceDetailQueryService) — if ever >1 (a future
    // parallel-gateway condition this engine does not support yet), CurrentTask is deliberately
    // null rather than an arbitrary guess (Part 6: "Do NOT silently choose one").
    int ActiveTaskCount,
    TaskDto? CurrentTask,
    IReadOnlyList<TaskDto> Tasks,
    TaskSlaDto? SlaSummary,
    // Phase 7.2.3 hardening — `SlaSummary.Status` is the raw `TaskSlaStatus` (no `Warning` value
    // exists there — see `TaskSlaDto`'s own shape). `SlaStatus` is the derived bucket
    // (`ProcessMonitoringSlaStatusMapper`), computed identically to Dashboard/Process Monitoring's
    // own SLA summaries, so all three views agree on whether a task is in Warning.
    ProcessMonitoringSlaStatus? SlaStatus,
    IReadOnlyList<ProcessProgressItemDto> WorkflowProgress,
    IReadOnlyList<ProcessTimelineItemDto> Timeline);

public interface IProcessInstanceDetailQueryService
{
    // currentUserId/currentUserRoles always come from the authenticated caller
    // (ICurrentUserService) — there is no way to pass another user's identity into this interface.
    // Returns null if the ProcessInstance does not exist; throws ForbiddenAppException if it
    // exists but the caller is not authorized — the exact same two outcomes
    // IProcessInstanceQueryService.GetByIdAsync already produces, reused directly rather than
    // reimplemented (Part 2).
    Task<ProcessInstanceDetailDto?> GetDetailAsync(Guid processInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);
}
