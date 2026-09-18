using BPM.Application.Common;
using BPM.Domain.Entities;

namespace BPM.Application.ProcessMonitoring;

// Phase 7.2.1 — mirrors TaskSlaStatus's real values (Active/Overdue/Completed) plus one derived
// bucket, Warning — the exact same split Phase 7.1's DashboardSlaSummaryDto already uses
// (TaskSla.Status == Active, split by whether WarningNotifiedAt has fired). A dedicated enum
// here (rather than reusing TaskSlaStatus directly) exists only so "Warning" can be a real,
// filterable/displayable value without adding a fake status to TaskSla itself — Phase 6.4
// deliberately has no persisted Warning status, and this phase does not introduce one either.
public enum ProcessMonitoringSlaStatus
{
    Active,
    Warning,
    Overdue,
    Completed,
}

// Phase 7.2.3 hardening — extracted so the Active/Warning split is computed exactly once, the same
// way, everywhere it's needed (ProcessMonitoringQueryService's page mapping and
// ProcessInstanceDetailQueryService's SlaStatus both call this now) rather than two independent
// copies of the same switch expression risking silent drift. Found and fixed as a genuine
// Dashboard/Monitoring/Detail SLA-consistency gap (Part 11): Process Instance Detail's
// `SlaSummary` (the existing, Phase 6.3 `TaskSlaDto`) has no `WarningNotifiedAt` field at all, so
// it could never have shown `Warning` even if it tried — `ProcessInstanceDetailDto.SlaStatus`
// (added this phase) is what actually carries the derived bucket, computed from the raw `TaskSla`
// entity's `Status`/`WarningNotifiedAt`, matching Dashboard/Process Monitoring exactly.
public static class ProcessMonitoringSlaStatusMapper
{
    public static ProcessMonitoringSlaStatus? From(TaskSlaStatus status, DateTime? warningNotifiedAt) => status switch
    {
        TaskSlaStatus.Active => warningNotifiedAt is null ? ProcessMonitoringSlaStatus.Active : ProcessMonitoringSlaStatus.Warning,
        TaskSlaStatus.Overdue => ProcessMonitoringSlaStatus.Overdue,
        TaskSlaStatus.Completed => ProcessMonitoringSlaStatus.Completed,
        _ => null,
    };
}

// Phase 7.2.1 — a dedicated, purpose-built projection; never the ProcessInstance entity itself
// (no DefinitionJson, no RowVersion, no internal workflow shape). Current-task fields are
// deliberately singular (CurrentTaskId, not CurrentTaskIds[]) because the workflow engine is
// currently strictly sequential — confirmed by inspection (ParallelGateway/JoinGateway are
// reserved node types, never executable; see WorkflowDefinitionValidator's own
// UNSUPPORTED_NODE_TYPE check) — so a ProcessInstance has at most one non-terminal TaskInstance at
// any time. ActiveTaskCount is included defensively (always 0 or 1 today) so this shape doesn't
// silently lie if a future phase ever adds parallel gateway support; see
// ProcessMonitoringQueryService's own comment on this decision.
public record ProcessMonitoringItemDto(
    Guid ProcessInstanceId,
    Guid ProcessDefinitionId,
    string ProcessDefinitionKey,
    string ProcessDefinitionName,
    ProcessInstanceStatus Status,
    Guid InitiatorId,
    string InitiatorDisplayName,
    DateTime StartedAt,
    DateTime UpdatedAt,
    Guid? CurrentTaskId,
    string? CurrentTaskName,
    TaskInstanceStatus? CurrentTaskStatus,
    bool CurrentTaskIsApprovalTask,
    // Never a single arbitrary candidate for an ApprovalTask with multiple candidates (Part 29) —
    // either a resolved user display name, "Role: X", or "Pending approval (N candidate(s))".
    string? CurrentTaskAssigneeDisplay,
    int ActiveTaskCount,
    ProcessMonitoringSlaStatus? SlaStatus,
    DateTime? SlaDueAt);

// Sort/filter values are a closed, whitelisted set — never a raw client-supplied SQL fragment
// (Part 13). Unrecognized SortBy values fall back to the default rather than erroring the whole
// request (a stray/typo'd query parameter degrades gracefully, matching this app's general
// tolerance for unknown query parameters elsewhere).
public enum ProcessMonitoringSortBy
{
    UpdatedAt,
    StartedAt,
    Status,
    ProcessDefinitionName,
    SlaDueAt,
}

public enum SortDirection
{
    Ascending,
    Descending,
}

public record ProcessMonitoringQuery(
    string? Search = null,
    ProcessInstanceStatus? Status = null,
    Guid? ProcessDefinitionId = null,
    Guid? InitiatorId = null,
    // Phase 7.3 — added for Reporting's Detail Table to reuse this query service directly rather
    // than duplicating its authorization/filter/sort/pagination logic (Part 13/14). Optional and
    // additive: existing callers (the Process Monitoring page) simply never set it. Filters by the
    // initiator's own User.DepartmentId — a real, existing relationship (User.DepartmentId),
    // never an invented one (Part 3).
    Guid? DepartmentId = null,
    ProcessMonitoringSlaStatus? SlaStatus = null,
    DateTime? StartedFrom = null,
    DateTime? StartedTo = null,
    ProcessMonitoringSortBy SortBy = ProcessMonitoringSortBy.UpdatedAt,
    SortDirection SortDirection = SortDirection.Descending,
    int Page = 1,
    int PageSize = 20);

// currentUserId/currentUserRoles always come from the authenticated caller
// (ICurrentUserService) — there is no way to pass another user's identity into this interface.
// Every filter in ProcessMonitoringQuery can only ever narrow the authorized result set that
// IProcessInstanceQueryService's own visibility rule already computes; none of them (InitiatorId
// included) can be used to broaden it — see ProcessMonitoringQueryService's own comment.
public interface IProcessMonitoringQueryService
{
    Task<PagedResult<ProcessMonitoringItemDto>> GetAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ProcessMonitoringQuery query, CancellationToken cancellationToken = default);
}
