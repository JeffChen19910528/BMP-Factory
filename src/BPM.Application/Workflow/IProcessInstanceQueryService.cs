using BPM.Application.Common;

namespace BPM.Application.Workflow;

public interface IProcessInstanceQueryService
{
    // Phase 5.5.4 hardening fix: previously took no caller identity at all — the same class of
    // gap Phase 5.4.3 found and fixed for ITaskQueryService.GetByIdAsync (see that interface's own
    // comment), just never applied here. Scoped to: Administrator, the process's own Initiator, or
    // anyone with a TaskInstance (direct/role assignee) or ApprovalAssignment participation on
    // this instance — the same visibility computation ITaskQueryService already uses, extended one
    // level up from "can you see this task" to "can you see this task's process instance."
    Task<IReadOnlyList<ProcessInstanceDto>> GetAllAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);
    Task<ProcessInstanceDto?> GetByIdAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);
}

public interface ITaskQueryService
{
    // Tasks the given user can act on right now: assigned to them directly, or open (Pending)
    // and assigned to a role they hold.
    //
    // Phase 12 — genuinely paginated at the database level (ORDER BY CreatedAt DESC, then
    // Skip/Take against TaskInstances, never a full unbounded fetch followed by an in-memory
    // slice) — see MyTasksQuery's own doc comment for why the default PageSize is generous rather
    // than the usual 20.
    Task<PagedResult<TaskDto>> GetMyTasksAsync(Guid userId, IReadOnlyCollection<string> userRoles, MyTasksQuery query, CancellationToken cancellationToken = default);

    // Phase 5.4.3 fix: previously took no caller identity at all — a genuine pre-existing IDOR
    // gap found during this phase's own mandatory inspection (GetMyTasksAsync already scopes to
    // the caller, but GetById returned any task to any authenticated user). Nothing called this
    // from real UI before Task Detail did, so the gap was latent rather than exploited, but this
    // phase is exactly what starts exercising it for real. Same visibility rule as
    // GetMyTasksAsync (direct/role assignee, or an approval participant) plus Administrator.
    Task<TaskDto?> GetByIdAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    // Phase 5.5.1 — Approvals Worklist. Scoped to exactly the ApprovalTask rows the caller is a
    // participant of (direct or delegated candidate on an ApprovalAssignment) — the same
    // authorization computation GetMyTasksAsync's approvalTaskIds already performs, reused rather
    // than duplicated, not a second approval-visibility rule. currentUserId is always derived
    // server-side from the authenticated caller (ICurrentUserService), never accepted as a query
    // parameter — see TasksController.
    Task<PagedResult<ApprovalWorklistItemDto>> GetApprovalWorklistAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ApprovalWorklistQuery query, CancellationToken cancellationToken = default);
}
