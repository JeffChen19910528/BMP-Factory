namespace BPM.Application.Dashboard;

// Phase 7.1 — Dashboard Foundation. All names deliberately prefixed `Dashboard*` even though a
// couple could otherwise read naturally as e.g. `ApprovalSummaryDto` — that exact name already
// exists in `BPM.Application.Workflow` (per-task approval progress: Policy/RequiredCount/
// ApprovedCount/Assignments), a completely different concept. Prefixing avoids the kind of
// same-simple-name collision this codebase has been bitten by before (see the Phase 6.2
// `BPM.Notification`/`Notification` namespace-collision lesson in CLAUDE.md) — cheap insurance,
// not paranoia.
//
// This is business data, not UI metadata (Part 9): no chart configuration, no colors, no frontend
// component settings. The frontend decides how to render these numbers; it never computes them.

// "Active" here means TaskInstanceStatus.Pending or InProgress — the two non-terminal statuses.
// Overdue/DueSoon come from TaskSla (Phase 6.3/6.4's own authoritative model), not recomputed here
// from DueAt/DateTime.Now — see DashboardQueryService's own comment on why.
public record DashboardTaskSummaryDto(int Total, int Overdue, int DueSoon);

// "Pending" = ApprovalAssignment rows where the caller is the direct or delegated candidate and
// the assignment itself is still Pending — the same participant predicate
// TaskQueryService/ApprovalEngine already use, just aggregated with a status filter, not a new
// approval-visibility rule.
public record DashboardApprovalSummaryDto(int Pending);

// Mirrors TaskSlaStatus (Phase 6.3/6.4) plus one derived bucket: Warning is not a persisted status
// (Phase 6.4 deliberately has no Warning status member — see TaskSlaStatus's own doc comment) but
// is represented here by splitting Active into "Active, no warning yet" vs "Active, WarningAt has
// already fired" using TaskSla.WarningNotifiedAt (the same persisted, scheduler-set idempotency
// field Phase 6.4 already uses) — never a fresh `WarningAt <= now` computation, which could
// disagree with what SlaSchedulerWorker itself has actually decided. Cancelled is omitted (it
// remains reserved/unreachable — see TaskSlaStatus's own doc comment; a field that is always zero
// buys nothing).
public record DashboardSlaSummaryDto(int Active, int Warning, int Overdue, int Completed);

// Mirrors the three ProcessInstanceStatus values the engine actually ever sets (Running/
// Completed/Rejected — confirmed by inspection before writing this DTO, Part 6's explicit
// instruction). Cancelled/Suspended/Failed are reserved-but-unreachable (no code path sets them
// anywhere in the engine, same precedent as several other reserved enum members across this
// codebase) and are deliberately not included as always-zero fields. "Returned" is NOT a
// ProcessInstanceStatus at all — Return rewinds a Task to a previous node within a still-Running
// process (see CLAUDE.md's own architectural rule); it has no process-level terminal state.
public record DashboardProcessSummaryDto(int Running, int Completed, int Rejected);

// A short, backend-authored, human-readable description (Part 9: business data, not a raw enum
// the frontend has to translate) — the same "backend decides display text" principle
// Notification.Title/Message already established in Phase 6.1. ProcessInstanceId is included only
// when resolvable, so the frontend can link to the existing Process Instance detail view where one
// exists; never a raw AuditLog EntityType/EntityId pair (that would leak internal audit-schema
// shape into the API contract).
public record DashboardActivityItemDto(DateTime Timestamp, string Action, string Description, Guid? ProcessInstanceId);

public record DashboardResponse(
    DashboardTaskSummaryDto MyTasks,
    DashboardApprovalSummaryDto PendingApprovals,
    DashboardSlaSummaryDto Sla,
    DashboardProcessSummaryDto ProcessOverview,
    IReadOnlyList<DashboardActivityItemDto> RecentActivity);

public interface IDashboardQueryService
{
    // currentUserId/currentUserRoles always come from the authenticated caller
    // (ICurrentUserService, via the controller) — there is no userId parameter anywhere on this
    // interface, matching every other caller-scoped query in this app (GetMyTasksAsync,
    // GetForUserAsync, etc.). Administrator gets a system-wide view; everyone else gets a view
    // scoped to their own authorized data, using the exact same authorization semantics
    // TaskQueryService/ProcessInstanceQueryService/ApprovalAssignment already enforce elsewhere —
    // this method introduces no new authorization rule of its own.
    Task<DashboardResponse> GetDashboardAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);
}
