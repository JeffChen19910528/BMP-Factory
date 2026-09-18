using BPM.Application.Common;
using BPM.Application.ProcessMonitoring;
using BPM.Domain.Entities;

namespace BPM.Application.Reports;

// Phase 7.3 — Reporting. Historical/aggregate view over the SAME authorized ProcessInstance scope
// Dashboard/Process Monitoring already compute (Part 14: Reporting answers "what happened during
// this period", Process Monitoring answers "what's happening now" — no duplicated business logic,
// only a duplicated *filter-composition* WHERE-clause, see ReportQueryService's own class comment
// for why that small amount of duplication was accepted over a shared-query refactor).
//
// Authorization flow is always: authorized ProcessInstance scope -> ReportQuery filters ->
// aggregation (Part 5) — never the reverse. Every DTO below is produced only after that ordering.

// Only the three ProcessInstanceStatus values the engine ever actually reaches (Part 7) —
// Cancelled/Suspended/Failed are reserved-but-unreachable everywhere else in this codebase (see
// DashboardProcessSummaryDto's own doc comment) and are not represented here either.
public record ReportProcessSummaryDto(int Total, int Running, int Completed, int Rejected);

public record ReportProcessBreakdownItemDto(
    Guid ProcessDefinitionId,
    string ProcessDefinitionKey,
    string ProcessDefinitionName,
    int Total,
    int Running,
    int Completed,
    int Rejected);

// Mirrors TaskInstanceStatus's real 7 persisted values 1:1 (Part 9) — no re-bucketing, no invented
// business meaning. Cancelled is reserved/unreachable today (same as TaskInstanceStatus.Cancelled
// everywhere else in this codebase) but is still reported as its own honest (always-zero) count
// rather than silently omitted.
public record ReportTaskSummaryDto(
    int Total,
    int Pending,
    int InProgress,
    int Completed,
    int Rejected,
    int Returned,
    int Cancelled,
    int Expired);

// Mirrors ApprovalAssignmentStatus's real 5 persisted values 1:1 (Part 10). This counts CURRENT
// assignment state only — the data model keeps no separate approval-action-history table (see
// CLAUDE.md's Approval Engine note: delegate is additive, transfer moves the same slot, so a
// candidate row's Status is always its latest, not an append-only log). Do not read "Approved: N"
// as "N approve actions were ever taken" — it is "N candidate rows are currently Approved". This
// limitation is deliberate and documented, not a gap Reporting attempts to close with a new
// approval-history subsystem.
public record ReportApprovalSummaryDto(
    int Total,
    int Pending,
    int Approved,
    int Rejected,
    int Returned,
    int Cancelled);

// Reuses the exact Active/Warning/Overdue/Completed bucket Dashboard/Process Monitoring already
// compute via ProcessMonitoringSlaStatusMapper — never a fifth invented bucket, never a client-
// side recalculation of SLA state from wall-clock time (Part 11/12: always the persisted TaskSla
// row, never DateTime.Now, never today's SlaPolicy reapplied to a historical task).
//
// SLA compliance definition (Part 11 — "define the calculation explicitly, least-assumptive
// interpretation"): only tasks whose TaskSla has actually reached Completed are counted at all —
// a still-Active/Warning/Overdue task is excluded from both the numerator and denominator (never
// silently counted as compliant just because it hasn't breached yet). Of those Completed rows,
// CompletedWithinSla = TaskSla.OverdueAt == null (never breached before completing);
// CompletedBreachedSla = TaskSla.OverdueAt != null (SlaEngine/SlaSchedulerWorker recorded a real
// overdue transition at some point before the task was eventually completed — OverdueAt, once
// set, is never cleared by completion, so it remains a reliable historical breach signal even
// though TaskSla.Status itself always ends at Completed either way).
// ComplianceRate = CompletedWithinSla / Completed, or null when Completed == 0 (avoiding a
// division-by-zero rather than reporting a misleading 0% or 100%).
public record ReportSlaSummaryDto(
    int Active,
    int Warning,
    int Overdue,
    int Completed,
    int CompletedWithinSla,
    int CompletedBreachedSla,
    double? ComplianceRate);

public record ReportSummaryResponse(
    ReportProcessSummaryDto Process,
    IReadOnlyList<ReportProcessBreakdownItemDto> ProcessBreakdown,
    ReportTaskSummaryDto TaskSummary,
    ReportApprovalSummaryDto ApprovalSummary,
    ReportSlaSummaryDto SlaSummary);

public enum ReportProcessBreakdownSortBy
{
    Total,
    ProcessDefinitionName,
}

// One shared filter(+pagination+sort) record, reused identically across Summary/Details/Export so
// the same filters always mean the same authorized+filtered scope everywhere (Part 31). Page/
// PageSize/SortBy/SortDirection are meaningful only for GetDetailsAsync (ignored by
// GetSummaryAsync/ExportCsvAsync — export emits all bounded rows in one deterministic order and
// never paginates). SortBy/SortDirection for the Detail Table deliberately reuse
// ProcessMonitoringSortBy/SortDirection wholesale, and GetDetailsAsync delegates directly to
// IProcessMonitoringQueryService.GetAsync — the Detail Table is not a second Process Monitoring
// implementation (Part 13/14).
//
// Date range semantics (Part 3/15): filters on ProcessInstance.StartedAt (never CreatedAt/UpdatedAt
// — UpdatedAt is not reliably populated for a still-Running process, see CLAUDE.md's Process
// Monitoring note), using the SAME inclusive `StartedAt >= From && StartedAt <= To` semantics
// ProcessMonitoringQuery.StartedFrom/StartedTo already use — a deliberate choice of consistency
// with the already-built, already-tested Detail Table query (Part 31) over Part 15's separately-
// stated preference for an exclusive upper bound; using two different boundary semantics for the
// same ReportQuery.To depending on which endpoint you called would be the worse inconsistency.
// All timestamps are UTC; the frontend may display local time but always sends/receives UTC.
public record ReportQuery(
    DateTime? From = null,
    DateTime? To = null,
    Guid? ProcessDefinitionId = null,
    ProcessInstanceStatus? Status = null,
    Guid? InitiatorId = null,
    Guid? DepartmentId = null,
    ProcessMonitoringSlaStatus? SlaStatus = null,
    int Page = 1,
    int PageSize = 20,
    ProcessMonitoringSortBy SortBy = ProcessMonitoringSortBy.StartedAt,
    SortDirection SortDirection = SortDirection.Descending,
    ReportProcessBreakdownSortBy BreakdownSortBy = ReportProcessBreakdownSortBy.Total);

// Export row cap (Part 23) — a deliberate, explicit safe maximum; exceeding it is a normal
// application error via the existing {code,message,traceId} contract, never an unbounded fetch.
public static class ReportExportLimits
{
    public const int MaxExportRows = 10000;
}

public interface IReportQueryService
{
    Task<ReportSummaryResponse> GetSummaryAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ReportQuery query, CancellationToken cancellationToken = default);

    Task<PagedResult<ProcessMonitoringItemDto>> GetDetailsAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ReportQuery query, CancellationToken cancellationToken = default);

    // Same authorized+filtered scope as GetDetailsAsync/GetSummaryAsync (Part 22 — export must
    // never be a separate visibility implementation), bounded by ReportExportLimits.MaxExportRows
    // (Part 23/48) and returned as ready-to-stream CSV bytes (Part 48 — bounded, not literally
    // streamed, since the row cap already bounds memory to a small, known-safe size).
    Task<byte[]> ExportCsvAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ReportQuery query, CancellationToken cancellationToken = default);
}
