using BPM.Application.Analytics;
using BPM.Application.Common;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Phase 7.4 — Analytics, read-only. Never modifies ProcessInstance/TaskInstance/TaskSla.
// Authorization: identical BuildAuthorizedFilteredScope pattern ReportQueryService already
// established (Part 5/6 — authorized scope computed BEFORE any aggregation, always via
// ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync, never a second visibility rule).
// This is a deliberate, small, accepted duplication of ReportQueryService's own scope-builder
// (Part 4: "may reuse Reporting query primitives... but do not force reuse where semantics
// differ") — the AUTHORIZATION computation itself is never duplicated, only composed the same
// way twice.
//
// Time bucketing and duration arithmetic: an initial implementation used
// EF.Functions.DateTrunc/DateDiffHour (PostgreSQL's own date_trunc + a date-diff), but neither
// has a translatable extension method in the installed Npgsql.EntityFrameworkCore.PostgreSQL
// 10.0.3 provider (confirmed by inspecting the shipped assembly — no such member exists), so EF
// throws at translation time. Rather than hand-composing raw SQL for this (a bigger, more fragile
// change than this phase's own Part 30 permits for a mere median calculation, and this codebase
// has never used raw SQL anywhere), every metric below projects only the small number of raw
// timestamp columns it needs — always still filtered by the caller's authorized+date-ranged scope
// first, never the unfiltered table — and computes bucketing/duration in C# over that bounded
// projection. This is a documented, evidence-based tradeoff (Part 29's "avoid ToListAsync-then-
// GroupBy" concern is about loading full, unfiltered entity sets; a projected, already-filtered,
// timestamp-only column list bounded by the query's own date range is a materially different,
// much smaller shape) — see PROGRESS.md's Phase 7.4 section for the full note.
public class AnalyticsQueryService : IAnalyticsQueryService
{
    private const string AdministratorRole = "Administrator";

    private readonly BpmDbContext _db;

    public AnalyticsQueryService(BpmDbContext db)
    {
        _db = db;
    }

    public async Task<AnalyticsOverviewResponse> GetOverviewAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, AnalyticsQuery query, CancellationToken cancellationToken = default)
    {
        ValidateDateRange(query);

        var isAdministrator = currentUserRoles.Contains(AdministratorRole);
        List<Guid>? visibleIds = isAdministrator
            ? null
            : await ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync(_db, currentUserId, currentUserRoles, cancellationToken);

        var scope = BuildAuthorizedFilteredScope(isAdministrator, visibleIds, query);
        var scopeIds = scope.Select(p => p.Id);

        var statusCounts = await scope.GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        int CountOf(ProcessInstanceStatus s) => statusCounts.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        var volumeTrend = await BuildVolumeTrendAsync(scope, query.Granularity, cancellationToken);
        var processDuration = await BuildProcessDurationAsync(scope, cancellationToken);
        var taskThroughput = await BuildTaskThroughputAsync(scopeIds, query.Granularity, cancellationToken);
        var taskDuration = await BuildTaskDurationAsync(scopeIds, cancellationToken);
        var nodeAnalytics = await BuildNodeAnalyticsAsync(scope, cancellationToken);
        var slaTrend = await BuildSlaTrendAsync(scopeIds, query.Granularity, cancellationToken);
        var processComparison = await BuildProcessComparisonAsync(scope, cancellationToken);

        return new AnalyticsOverviewResponse(
            TotalProcesses: statusCounts.Sum(x => x.Count),
            RunningProcesses: CountOf(ProcessInstanceStatus.Running),
            CompletedProcesses: CountOf(ProcessInstanceStatus.Completed),
            RejectedProcesses: CountOf(ProcessInstanceStatus.Rejected),
            VolumeTrend: volumeTrend,
            ProcessDuration: processDuration,
            TaskThroughputTrend: taskThroughput,
            TaskDuration: taskDuration,
            NodeAnalytics: nodeAnalytics,
            SlaTrend: slaTrend,
            ProcessComparison: processComparison);
    }

    private static void ValidateDateRange(AnalyticsQuery query)
    {
        if (query.From is not null && query.To is not null && query.From > query.To)
        {
            throw new BadRequestAppException("ANALYTICS_INVALID_DATE_RANGE", "The analytics query's 'From' date must not be after its 'To' date.");
        }
    }

    // Part 31 — a fixed, three-branch whitelist; never string-built from request input.
    private static DateTime BucketOf(DateTime timestamp, AnalyticsGranularity granularity) => granularity switch
    {
        AnalyticsGranularity.Week => timestamp.Date.AddDays(-(int)timestamp.DayOfWeek), // Sunday-start week bucket
        AnalyticsGranularity.Month => new DateTime(timestamp.Year, timestamp.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => timestamp.Date,
    };

    private IQueryable<ProcessInstance> BuildAuthorizedFilteredScope(bool isAdministrator, List<Guid>? visibleIds, AnalyticsQuery query)
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

    // Part 9/29 — three small, already-filtered timestamp-only projections, bucketed in C# (see
    // class comment on why DB-side date_trunc isn't available here), then merged over at-most-
    // N-buckets rows, never over full instance rows.
    private static async Task<IReadOnlyList<VolumeTrendPointDto>> BuildVolumeTrendAsync(IQueryable<ProcessInstance> scope, AnalyticsGranularity granularity, CancellationToken cancellationToken)
    {
        var startedAt = await scope.Select(p => p.StartedAt).ToListAsync(cancellationToken);
        var completedAt = await scope.Where(p => p.Status == ProcessInstanceStatus.Completed && p.CompletedAt != null)
            .Select(p => p.CompletedAt!.Value).ToListAsync(cancellationToken);
        var rejectedAt = await scope.Where(p => p.Status == ProcessInstanceStatus.Rejected && p.CompletedAt != null)
            .Select(p => p.CompletedAt!.Value).ToListAsync(cancellationToken);

        var started = startedAt.GroupBy(t => BucketOf(t, granularity)).ToDictionary(g => g.Key, g => g.Count());
        var completed = completedAt.GroupBy(t => BucketOf(t, granularity)).ToDictionary(g => g.Key, g => g.Count());
        var rejected = rejectedAt.GroupBy(t => BucketOf(t, granularity)).ToDictionary(g => g.Key, g => g.Count());

        var buckets = started.Keys.Concat(completed.Keys).Concat(rejected.Keys).Distinct().OrderBy(b => b);

        return buckets.Select(b => new VolumeTrendPointDto(
            BucketStart: b,
            Started: started.GetValueOrDefault(b),
            Completed: completed.GetValueOrDefault(b),
            Rejected: rejected.GetValueOrDefault(b))).ToList();
    }

    // Part 11 — StartedAt -> CompletedAt, Completed instances only; a Running/Rejected/no-
    // CompletedAt row is excluded entirely, never coerced to a zero/negative duration.
    private static async Task<DurationStatsDto> BuildProcessDurationAsync(IQueryable<ProcessInstance> scope, CancellationToken cancellationToken)
    {
        var pairs = await scope
            .Where(p => p.Status == ProcessInstanceStatus.Completed && p.CompletedAt != null)
            .Select(p => new { p.StartedAt, Completed = p.CompletedAt!.Value })
            .ToListAsync(cancellationToken);

        return Stats(pairs.Select(p => (p.Completed - p.StartedAt).TotalHours));
    }

    private async Task<IReadOnlyList<TaskThroughputPointDto>> BuildTaskThroughputAsync(IQueryable<Guid> scopeIds, AnalyticsGranularity granularity, CancellationToken cancellationToken)
    {
        var createdAt = await _db.TaskInstances.AsNoTracking()
            .Where(t => scopeIds.Contains(t.ProcessInstanceId))
            .Select(t => t.CreatedAt).ToListAsync(cancellationToken);
        var completedAt = await _db.TaskInstances.AsNoTracking()
            .Where(t => scopeIds.Contains(t.ProcessInstanceId) && t.Status == TaskInstanceStatus.Completed && t.CompletedAt != null)
            .Select(t => t.CompletedAt!.Value).ToListAsync(cancellationToken);

        var created = createdAt.GroupBy(t => BucketOf(t, granularity)).ToDictionary(g => g.Key, g => g.Count());
        var completed = completedAt.GroupBy(t => BucketOf(t, granularity)).ToDictionary(g => g.Key, g => g.Count());
        var buckets = created.Keys.Concat(completed.Keys).Distinct().OrderBy(b => b);

        return buckets.Select(b => new TaskThroughputPointDto(
            BucketStart: b,
            Created: created.GetValueOrDefault(b),
            Completed: completed.GetValueOrDefault(b))).ToList();
    }

    // Part 13/14 — TaskInstance.StartedAt is confirmed (by inspection — grepped every assignment
    // site in the engine) never populated anywhere; CreatedAt -> CompletedAt is the only reliable
    // basis, for Completed tasks only.
    private async Task<DurationStatsDto> BuildTaskDurationAsync(IQueryable<Guid> scopeIds, CancellationToken cancellationToken)
    {
        var pairs = await _db.TaskInstances.AsNoTracking()
            .Where(t => scopeIds.Contains(t.ProcessInstanceId) && t.Status == TaskInstanceStatus.Completed && t.CompletedAt != null)
            .Select(t => new { t.CreatedAt, Completed = t.CompletedAt!.Value })
            .ToListAsync(cancellationToken);

        return Stats(pairs.Select(p => (p.Completed - p.CreatedAt).TotalHours));
    }

    // Part 15/17 — grouped by (ProcessDefinitionId, NodeId) to avoid conflating same-id nodes
    // across unrelated process definitions (NodeId is a string scoped to one ProcessVersion's
    // own JSON, not globally unique). OverdueCount via TaskSla.OverdueAt (never recalculated SLA
    // state). Node grouping itself stays DB-side (GroupBy over TaskInstance, a standard
    // translatable shape); only the duration figures per node use the same bounded-projection
    // approach as the other duration metrics.
    private async Task<IReadOnlyList<NodeAnalyticsItemDto>> BuildNodeAnalyticsAsync(IQueryable<ProcessInstance> scope, CancellationToken cancellationToken)
    {
        var scopeIds = scope.Select(p => p.Id);

        var grouped = await _db.TaskInstances.AsNoTracking()
            .Where(t => scopeIds.Contains(t.ProcessInstanceId))
            .GroupBy(t => new { t.ProcessInstance!.ProcessDefinitionId, t.NodeId })
            .Select(g => new
            {
                g.Key.ProcessDefinitionId,
                g.Key.NodeId,
                NodeName = g.OrderByDescending(t => t.CreatedAt).Select(t => t.NodeName).First(),
                Executions = g.Count(),
                Completed = g.Count(t => t.Status == TaskInstanceStatus.Completed),
            })
            .ToListAsync(cancellationToken);

        // Phase 10 — this used to issue two further queries per distinct (ProcessDefinitionId,
        // NodeId) group in the loop below (a 2N+1 pattern that got slower as the number of
        // distinct workflow steps in the authorized+filtered scope grew). Both are now fetched
        // once, for every node at once, and grouped in memory instead — the same "bounded
        // follow-up query, never per-row/per-group" discipline this codebase already uses
        // elsewhere (TaskQueryService.LoadTasksAsync, DashboardQueryService.RecentActivityAsync).
        var durationRows = await _db.TaskInstances.AsNoTracking()
            .Where(t => scopeIds.Contains(t.ProcessInstanceId) && t.Status == TaskInstanceStatus.Completed && t.CompletedAt != null)
            .Select(t => new { t.ProcessInstance!.ProcessDefinitionId, t.NodeId, t.CreatedAt, Completed = t.CompletedAt!.Value })
            .ToListAsync(cancellationToken);
        var durationsByNode = durationRows
            .GroupBy(r => (r.ProcessDefinitionId, r.NodeId))
            .ToDictionary(g => g.Key, g => g.Select(r => (r.Completed - r.CreatedAt).TotalHours));

        var overdueRows = await (
            from s in _db.TaskSlas.AsNoTracking()
            join t in _db.TaskInstances.AsNoTracking() on s.TaskInstanceId equals t.Id
            where s.OverdueAt != null && scopeIds.Contains(t.ProcessInstanceId)
            select new { t.ProcessInstance!.ProcessDefinitionId, t.NodeId }
        ).ToListAsync(cancellationToken);
        var overdueCountByNode = overdueRows
            .GroupBy(r => (r.ProcessDefinitionId, r.NodeId))
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new List<NodeAnalyticsItemDto>();
        foreach (var node in grouped)
        {
            var key = (node.ProcessDefinitionId, node.NodeId);
            var durations = durationsByNode.TryGetValue(key, out var d) ? d : Enumerable.Empty<double>();
            var overdueCount = overdueCountByNode.TryGetValue(key, out var c) ? c : 0;

            result.Add(new NodeAnalyticsItemDto(node.NodeId, node.NodeName, node.Executions, node.Completed, Stats(durations), overdueCount));
        }

        return result.OrderByDescending(n => n.Executions).ToList();
    }

    // Part 18/19/20 — bucketed by TaskSla.CompletedAt, Completed rows only; compliance defined
    // identically to ReportSlaSummaryDto (Part 19's own instruction to reuse, not reinvent).
    private async Task<IReadOnlyList<SlaTrendPointDto>> BuildSlaTrendAsync(IQueryable<Guid> scopeIds, AnalyticsGranularity granularity, CancellationToken cancellationToken)
    {
        var rows = await (
            from s in _db.TaskSlas.AsNoTracking()
            join t in _db.TaskInstances.AsNoTracking() on s.TaskInstanceId equals t.Id
            where scopeIds.Contains(t.ProcessInstanceId) && s.Status == TaskSlaStatus.Completed && s.CompletedAt != null
            select new { CompletedAt = s.CompletedAt!.Value, s.OverdueAt }
        ).ToListAsync(cancellationToken);

        var buckets = rows.GroupBy(r => BucketOf(r.CompletedAt, granularity))
            .Select(g => new
            {
                Bucket = g.Key,
                Total = g.Count(),
                Compliant = g.Count(r => r.OverdueAt == null),
                Breached = g.Count(r => r.OverdueAt != null),
            })
            .OrderBy(x => x.Bucket);

        return buckets.Select(r => new SlaTrendPointDto(
            r.Bucket, r.Total, r.Compliant, r.Breached,
            r.Total == 0 ? null : (double)r.Compliant / r.Total)).ToList();
    }

    // Part 23 — one definition-comparison view, sharing the exact same duration/compliance
    // definitions as ProcessDuration/SlaTrend above (never a second interpretation).
    //
    // Phase 12 — this used to issue two further queries per distinct ProcessDefinitionId in the
    // loop below (a 2N+1 pattern, the same shape Phase 10 already fixed once in
    // BuildNodeAnalyticsAsync but explicitly left out of that phase's scope). Confirmed via a real
    // measurement against the live dev database that this was the dominant cost of
    // GET /api/analytics/overview (987 distinct process definitions with instances -> ~1,975
    // queries in that one call). Both per-item queries are now fetched once, for every process
    // definition at once, and grouped in memory instead — the identical "bounded follow-up query,
    // never per-row/per-group" discipline BuildNodeAnalyticsAsync's own Phase 10 fix established.
    private async Task<IReadOnlyList<ProcessComparisonItemDto>> BuildProcessComparisonAsync(IQueryable<ProcessInstance> scope, CancellationToken cancellationToken)
    {
        var grouped = await (
            from p in scope
            join pd in _db.ProcessDefinitions.AsNoTracking() on p.ProcessDefinitionId equals pd.Id
            group p by new { pd.Id, pd.Key, pd.Name } into g
            select new
            {
                g.Key.Id,
                g.Key.Key,
                g.Key.Name,
                Total = g.Count(),
                Completed = g.Count(x => x.Status == ProcessInstanceStatus.Completed),
                Rejected = g.Count(x => x.Status == ProcessInstanceStatus.Rejected),
            }).ToListAsync(cancellationToken);

        var durationRows = await scope
            .Where(p => p.Status == ProcessInstanceStatus.Completed && p.CompletedAt != null)
            .Select(p => new { p.ProcessDefinitionId, p.StartedAt, Completed = p.CompletedAt!.Value })
            .ToListAsync(cancellationToken);
        var durationsByDefinition = durationRows
            .GroupBy(r => r.ProcessDefinitionId)
            .ToDictionary(g => g.Key, g => g.Select(r => (r.Completed - r.StartedAt).TotalHours));

        var scopeIds = scope.Select(p => p.Id);
        var slaRows = await (
            from s in _db.TaskSlas.AsNoTracking()
            join t in _db.TaskInstances.AsNoTracking() on s.TaskInstanceId equals t.Id
            where scopeIds.Contains(t.ProcessInstanceId)
            select new { t.ProcessInstance!.ProcessDefinitionId, s.Status, s.OverdueAt }
        ).ToListAsync(cancellationToken);
        var slaByDefinition = slaRows.GroupBy(r => r.ProcessDefinitionId).ToDictionary(g => g.Key, g => g.ToList());
        var emptySlaRows = slaRows.Take(0).ToList(); // same anonymous type as slaByDefinition's values, O(1) to build

        var result = new List<ProcessComparisonItemDto>();
        foreach (var item in grouped)
        {
            var durations = durationsByDefinition.TryGetValue(item.Id, out var d) ? d : Enumerable.Empty<double>();
            var definitionSlaRows = slaByDefinition.TryGetValue(item.Id, out var rows) ? rows : emptySlaRows;

            var completedSla = definitionSlaRows.Where(s => s.Status == TaskSlaStatus.Completed).ToList();
            double? complianceRate = completedSla.Count == 0 ? null : (double)completedSla.Count(s => s.OverdueAt == null) / completedSla.Count;
            var overdueCount = definitionSlaRows.Count(s => s.OverdueAt != null);

            result.Add(new ProcessComparisonItemDto(item.Id, item.Key, item.Name, item.Total, item.Completed, item.Rejected,
                Stats(durations), complianceRate, overdueCount));
        }

        return result.OrderByDescending(r => r.Total).ToList();
    }

    private static DurationStatsDto Stats(IEnumerable<double> hoursEnumerable)
    {
        var hours = hoursEnumerable.ToList();
        return hours.Count == 0
            ? new DurationStatsDto(0, null, null, null)
            : new DurationStatsDto(hours.Count, hours.Average(), hours.Min(), hours.Max());
    }
}
