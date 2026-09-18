using System.Text;
using BPM.Application.Common;
using BPM.Application.ProcessMonitoring;
using BPM.Application.Reports;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Phase 7.3 — Reporting, read-only. Never modifies ProcessInstance/TaskInstance/
// ApprovalAssignment/TaskSla. Reuses ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync
// verbatim for authorization (same as ProcessMonitoringQueryService/DashboardQueryService) — no
// second "which process instances can this user see" implementation exists here (Part 5/30).
//
// Authorization is always applied to the base ProcessInstance query BEFORE any ReportQuery filter
// and BEFORE any aggregation runs (Part 5/6) — BuildAuthorizedFilteredScope is the single place
// that composes {authorization} + {From/To/ProcessDefinitionId/Status/InitiatorId/DepartmentId},
// and every aggregate method (GetSummaryAsync's five DTOs) as well as GetDetailsAsync/
// ExportCsvAsync start from either that exact scope or delegate to
// IProcessMonitoringQueryService.GetAsync with the identical filters translated across — so
// Summary/Breakdown/TaskSummary/ApprovalSummary/SlaSummary/Details/Export can never disagree about
// which process instances are in scope for the same ReportQuery (Part 31).
//
// GetDetailsAsync/ExportCsvAsync deliberately do NOT reuse BuildAuthorizedFilteredScope directly —
// they delegate to IProcessMonitoringQueryService.GetAsync instead, reusing its existing DTO
// (ProcessMonitoringItemDto) and its existing SLA-join/search/sort/pagination logic wholesale
// (Part 13/14: "do not build a second independent Process Monitoring implementation"). This does
// mean the ReportQuery -> ProcessMonitoringQuery filter translation (From/To/ProcessDefinitionId/
// Status/InitiatorId/DepartmentId) is composed in two places (here, and again inside
// BuildAuthorizedFilteredScope) rather than one shared helper — a deliberate, small, mechanical
// duplication (each is a one-line predicate) accepted over forcing IProcessMonitoringQueryService
// and the aggregate queries into one shared query-builder abstraction purely for theoretical reuse
// (Part 14's own explicit warning against a refactor with no other justification). The
// AUTHORIZATION computation itself is never duplicated — both paths call
// ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync, the one canonical source.
public class ReportQueryService : IReportQueryService
{
    private const string AdministratorRole = "Administrator";

    private readonly BpmDbContext _db;
    private readonly IProcessMonitoringQueryService _processMonitoringQueryService;

    public ReportQueryService(BpmDbContext db, IProcessMonitoringQueryService processMonitoringQueryService)
    {
        _db = db;
        _processMonitoringQueryService = processMonitoringQueryService;
    }

    public async Task<ReportSummaryResponse> GetSummaryAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ReportQuery query, CancellationToken cancellationToken = default)
    {
        ValidateDateRange(query);

        var isAdministrator = currentUserRoles.Contains(AdministratorRole);
        List<Guid>? visibleIds = isAdministrator
            ? null
            : await ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync(_db, currentUserId, currentUserRoles, cancellationToken);

        var scope = BuildAuthorizedFilteredScope(isAdministrator, visibleIds, query);
        var scopeIds = scope.Select(p => p.Id);

        var processSummary = await BuildProcessSummaryAsync(scope, cancellationToken);
        var breakdown = await BuildProcessBreakdownAsync(scope, query.BreakdownSortBy, cancellationToken);
        var taskSummary = await BuildTaskSummaryAsync(scopeIds, cancellationToken);
        var approvalSummary = await BuildApprovalSummaryAsync(scopeIds, cancellationToken);
        var slaSummary = await BuildSlaSummaryAsync(scopeIds, cancellationToken);

        return new ReportSummaryResponse(processSummary, breakdown, taskSummary, approvalSummary, slaSummary);
    }

    public async Task<PagedResult<ProcessMonitoringItemDto>> GetDetailsAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ReportQuery query, CancellationToken cancellationToken = default)
    {
        ValidateDateRange(query);
        var monitoringQuery = ToProcessMonitoringQuery(query);
        return await _processMonitoringQueryService.GetAsync(currentUserId, currentUserRoles, monitoringQuery, cancellationToken);
    }

    public async Task<byte[]> ExportCsvAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ReportQuery query, CancellationToken cancellationToken = default)
    {
        ValidateDateRange(query);

        // Request one row beyond the cap so an over-limit result is detected without ever
        // materializing more than MaxExportRows + 1 rows (Part 19/23 — no unbounded fetch).
        var monitoringQuery = ToProcessMonitoringQuery(query) with { Page = 1, PageSize = ReportExportLimits.MaxExportRows + 1 };
        var result = await _processMonitoringQueryService.GetAsync(currentUserId, currentUserRoles, monitoringQuery, cancellationToken);

        if (result.TotalCount > ReportExportLimits.MaxExportRows)
        {
            throw new BadRequestAppException(
                "REPORT_EXPORT_TOO_LARGE",
                $"The export would contain {result.TotalCount} rows, which exceeds the maximum of {ReportExportLimits.MaxExportRows}. Narrow the filters (date range, process, status) and try again.");
        }

        return BuildCsv(result.Items);
    }

    private static void ValidateDateRange(ReportQuery query)
    {
        if (query.From is not null && query.To is not null && query.From > query.To)
        {
            throw new BadRequestAppException("REPORT_INVALID_DATE_RANGE", "The report's 'From' date must not be after its 'To' date.");
        }
    }

    private static ProcessMonitoringQuery ToProcessMonitoringQuery(ReportQuery query) => new(
        Status: query.Status,
        ProcessDefinitionId: query.ProcessDefinitionId,
        InitiatorId: query.InitiatorId,
        DepartmentId: query.DepartmentId,
        SlaStatus: query.SlaStatus,
        StartedFrom: query.From,
        StartedTo: query.To,
        SortBy: query.SortBy,
        SortDirection: query.SortDirection,
        Page: query.Page,
        PageSize: query.PageSize);

    // The one place authorization + ReportQuery's process-level filters are composed for every
    // aggregate DTO in GetSummaryAsync. Deliberately excludes SlaStatus (a task-level concept —
    // meaningful for the Detail Table/export row filter, not for scoping which ProcessInstances
    // feed the five summary aggregates) and pagination/sort (aggregates never paginate).
    private IQueryable<ProcessInstance> BuildAuthorizedFilteredScope(bool isAdministrator, List<Guid>? visibleIds, ReportQuery query)
    {
        var scope = _db.ProcessInstances.AsNoTracking().AsQueryable();
        if (!isAdministrator)
        {
            scope = scope.Where(p => visibleIds!.Contains(p.Id));
        }
        if (query.From is not null)
        {
            scope = scope.Where(p => p.StartedAt >= query.From);
        }
        if (query.To is not null)
        {
            scope = scope.Where(p => p.StartedAt <= query.To);
        }
        if (query.ProcessDefinitionId is not null)
        {
            scope = scope.Where(p => p.ProcessDefinitionId == query.ProcessDefinitionId);
        }
        if (query.Status is not null)
        {
            scope = scope.Where(p => p.Status == query.Status);
        }
        if (query.InitiatorId is not null)
        {
            scope = scope.Where(p => p.InitiatorId == query.InitiatorId);
        }
        if (query.DepartmentId is not null)
        {
            var deptInitiatorIds = _db.Users.AsNoTracking().Where(u => u.DepartmentId == query.DepartmentId).Select(u => u.Id);
            scope = scope.Where(p => deptInitiatorIds.Contains(p.InitiatorId));
        }
        return scope;
    }

    // Part 7/17 — one DB-side GroupBy over the (already bounded-by-filters) authorized scope, no
    // ToListAsync()-then-in-memory grouping over every ProcessInstance.
    private static async Task<ReportProcessSummaryDto> BuildProcessSummaryAsync(IQueryable<ProcessInstance> scope, CancellationToken cancellationToken)
    {
        var counts = await scope.GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountOf(ProcessInstanceStatus status) => counts.FirstOrDefault(x => x.Status == status)?.Count ?? 0;

        return new ReportProcessSummaryDto(
            Total: counts.Sum(x => x.Count),
            Running: CountOf(ProcessInstanceStatus.Running),
            Completed: CountOf(ProcessInstanceStatus.Completed),
            Rejected: CountOf(ProcessInstanceStatus.Rejected));
    }

    // Part 8/17 — one DB-side join+GroupBy by ProcessDefinition. The result set is bounded by the
    // number of distinct ProcessDefinitions represented in scope (never by instance count), so
    // sorting the already-small materialized list in memory (below) is not the "load everything,
    // sort client-side" anti-pattern Part 8 warns against — that refers to the Detail Table's
    // per-instance rows, which stay entirely DB-side/paginated via IProcessMonitoringQueryService.
    private async Task<IReadOnlyList<ReportProcessBreakdownItemDto>> BuildProcessBreakdownAsync(IQueryable<ProcessInstance> scope, ReportProcessBreakdownSortBy sortBy, CancellationToken cancellationToken)
    {
        var raw = await (
            from p in scope
            join pd in _db.ProcessDefinitions.AsNoTracking() on p.ProcessDefinitionId equals pd.Id
            group p by new { pd.Id, pd.Key, pd.Name } into g
            select new
            {
                g.Key.Id,
                g.Key.Key,
                g.Key.Name,
                Total = g.Count(),
                Running = g.Count(x => x.Status == ProcessInstanceStatus.Running),
                Completed = g.Count(x => x.Status == ProcessInstanceStatus.Completed),
                Rejected = g.Count(x => x.Status == ProcessInstanceStatus.Rejected),
            }).ToListAsync(cancellationToken);

        var ordered = sortBy == ReportProcessBreakdownSortBy.ProcessDefinitionName
            ? raw.OrderBy(x => x.Name)
            : raw.OrderByDescending(x => x.Total).ThenBy(x => x.Name);

        return ordered.Select(x => new ReportProcessBreakdownItemDto(x.Id, x.Key, x.Name, x.Total, x.Running, x.Completed, x.Rejected)).ToList();
    }

    // Part 9/17/18 — counts every TaskInstance ever created for a ProcessInstance in scope
    // (historical, matching Reporting's "what happened" framing — not just currently-active tasks
    // the way Dashboard's MyTasks summary does), via one GroupBy, no per-row lookup.
    private async Task<ReportTaskSummaryDto> BuildTaskSummaryAsync(IQueryable<Guid> scopeIds, CancellationToken cancellationToken)
    {
        var counts = await _db.TaskInstances.AsNoTracking()
            .Where(t => scopeIds.Contains(t.ProcessInstanceId))
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountOf(TaskInstanceStatus status) => counts.FirstOrDefault(x => x.Status == status)?.Count ?? 0;

        return new ReportTaskSummaryDto(
            Total: counts.Sum(x => x.Count),
            Pending: CountOf(TaskInstanceStatus.Pending),
            InProgress: CountOf(TaskInstanceStatus.InProgress),
            Completed: CountOf(TaskInstanceStatus.Completed),
            Rejected: CountOf(TaskInstanceStatus.Rejected),
            Returned: CountOf(TaskInstanceStatus.Returned),
            Cancelled: CountOf(TaskInstanceStatus.Cancelled),
            Expired: CountOf(TaskInstanceStatus.Expired));
    }

    // Part 10/17/18 — mirrors ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync's own
    // `byApproval` navigation-chain filter style (already proven to translate correctly), just
    // grouped by Status instead of filtered by candidate identity. Counts CURRENT
    // ApprovalAssignment rows only — see ReportApprovalSummaryDto's own doc comment on why this is
    // not, and cannot be, a historical action count.
    private async Task<ReportApprovalSummaryDto> BuildApprovalSummaryAsync(IQueryable<Guid> scopeIds, CancellationToken cancellationToken)
    {
        var counts = await _db.ApprovalAssignments.AsNoTracking()
            .Where(a => scopeIds.Contains(a.ApprovalInstance!.TaskInstance!.ProcessInstanceId))
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountOf(ApprovalAssignmentStatus status) => counts.FirstOrDefault(x => x.Status == status)?.Count ?? 0;

        return new ReportApprovalSummaryDto(
            Total: counts.Sum(x => x.Count),
            Pending: CountOf(ApprovalAssignmentStatus.Pending),
            Approved: CountOf(ApprovalAssignmentStatus.Approved),
            Rejected: CountOf(ApprovalAssignmentStatus.Rejected),
            Returned: CountOf(ApprovalAssignmentStatus.Returned),
            Cancelled: CountOf(ApprovalAssignmentStatus.Cancelled));
    }

    // Part 11/12/17/18 — reads only the persisted TaskSla row (never recalculates from
    // DateTime.Now, never reinterprets with today's SlaPolicy — see ReportSlaSummaryDto's own doc
    // comment for the exact compliance definition). One GroupBy for the Active/Warning/Overdue/
    // Completed bucket counts (reusing ProcessMonitoringSlaStatusMapper so this can never disagree
    // with Dashboard/Process Monitoring's own bucketing for the same TaskSla row) plus one
    // aggregate query for the compliance figures.
    private async Task<ReportSlaSummaryDto> BuildSlaSummaryAsync(IQueryable<Guid> scopeIds, CancellationToken cancellationToken)
    {
        var slaRows = await (
            from s in _db.TaskSlas.AsNoTracking()
            join t in _db.TaskInstances.AsNoTracking() on s.TaskInstanceId equals t.Id
            where scopeIds.Contains(t.ProcessInstanceId)
            select new { s.Status, s.WarningNotifiedAt, s.OverdueAt }
        ).ToListAsync(cancellationToken);

        int active = 0, warning = 0, overdue = 0, completed = 0, completedWithinSla = 0, completedBreachedSla = 0;
        foreach (var row in slaRows)
        {
            var bucket = ProcessMonitoringSlaStatusMapper.From(row.Status, row.WarningNotifiedAt);
            switch (bucket)
            {
                case ProcessMonitoringSlaStatus.Active: active++; break;
                case ProcessMonitoringSlaStatus.Warning: warning++; break;
                case ProcessMonitoringSlaStatus.Overdue: overdue++; break;
                case ProcessMonitoringSlaStatus.Completed:
                    completed++;
                    if (row.OverdueAt is null) { completedWithinSla++; } else { completedBreachedSla++; }
                    break;
            }
        }

        double? complianceRate = completed == 0 ? null : (double)completedWithinSla / completed;

        return new ReportSlaSummaryDto(active, warning, overdue, completed, completedWithinSla, completedBreachedSla, complianceRate);
    }

    // Part 21/45/48 — CSV formula-injection defense (a leading =, +, -, @ is prefixed with a
    // literal single quote so spreadsheet software treats the cell as text, never a formula) plus
    // standard RFC4180 quoting (a field containing a comma, quote, or newline is wrapped in quotes
    // with embedded quotes doubled). A UTF-8 BOM is prepended so Excel opens non-ASCII (e.g.
    // Chinese) text correctly rather than mis-detecting the encoding. Only display-safe fields
    // already exposed by ProcessMonitoringItemDto are included — never FormData, tokens, or any
    // other sensitive field (Part 45); this is the exact same data/authorization the on-screen
    // Detail Table already shows for an identical filter (Part 22/31).
    private static byte[] BuildCsv(IReadOnlyList<ProcessMonitoringItemDto> items)
    {
        var sb = new StringBuilder();
        sb.Append(CsvRow("ProcessInstanceId", "ProcessDefinitionKey", "ProcessDefinitionName", "Status", "InitiatorDisplayName", "StartedAt", "UpdatedAt", "CurrentTaskName", "SlaStatus", "SlaDueAt"));
        foreach (var item in items)
        {
            sb.Append(CsvRow(
                item.ProcessInstanceId.ToString(),
                item.ProcessDefinitionKey,
                item.ProcessDefinitionName,
                item.Status.ToString(),
                item.InitiatorDisplayName,
                item.StartedAt.ToString("O"),
                item.UpdatedAt.ToString("O"),
                item.CurrentTaskName ?? string.Empty,
                item.SlaStatus?.ToString() ?? string.Empty,
                item.SlaDueAt?.ToString("O") ?? string.Empty));
        }

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static string CsvRow(params string[] fields) => string.Join(",", fields.Select(CsvField)) + "\r\n";

    private static readonly char[] FormulaTriggerChars = { '=', '+', '-', '@' };

    private static string CsvField(string? value)
    {
        value ??= string.Empty;

        if (value.Length > 0 && FormulaTriggerChars.Contains(value[0]))
        {
            value = "'" + value;
        }

        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
        {
            value = "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
