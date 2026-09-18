using BPM.Application.Common;
using BPM.Domain.Entities;

namespace BPM.Application.Analytics;

// Phase 7.4 — Analytics. Historical trends/durations/bottleneck-descriptive metrics over the same
// authorized ProcessInstance scope Dashboard/Process Monitoring/Reporting already compute —
// "how is behavior changing / where is time being spent" rather than Reporting's "what happened."
// Descriptive only: no prediction, no ML, no anomaly detection, no automated recommendations
// (explicitly out of scope). Every metric below has an explicit, documented definition — see each
// DTO's own comment for Numerator/Denominator/Source timestamp/Null behavior, per this phase's own
// Part 1 requirement to define metrics before implementing them.

public enum AnalyticsGranularity
{
    Day,
    Week,
    Month,
}

// One shared filter model, reused by the single overview endpoint (Part 28 — no per-metric copy).
// Date semantics: filters ProcessInstance.StartedAt, inclusive on both ends — identical to
// ReportQuery's own documented choice, for the same reason (consistency with the rest of Phase 7,
// Part 48/49/50's cross-check requirement). UTC internally.
public record AnalyticsQuery(
    DateTime? From = null,
    DateTime? To = null,
    Guid? ProcessDefinitionId = null,
    ProcessInstanceStatus? Status = null,
    Guid? InitiatorId = null,
    Guid? DepartmentId = null,
    AnalyticsGranularity Granularity = AnalyticsGranularity.Day);

// Metric: Duration statistics (used for Process Duration and Task Duration).
// Numerator/basis: CompletedAt - <start timestamp>, in hours, for rows that have BOTH a start and
// a completion timestamp — a row missing either is excluded entirely (Part 14: "do not convert
// missing duration to zero, do not distort averages with invalid rows"), never coerced to zero.
// SampleCount is always reported alongside (Part 17) so a caller can judge reliability — an
// AverageHours of "4.2" over a SampleCount of 1 means something different from over 200.
// Median: deliberately NOT implemented. PostgreSQL's `PERCENTILE_CONT` has no native EF Core LINQ
// translation, and hand-composing raw SQL for one statistic — for a codebase whose established
// EF Core query patterns (ReportQueryService, ProcessMonitoringQueryService) never use raw SQL
// anywhere — is exactly the "fragile SQL solely to obtain median" this phase's own Part 30
// instructs against; omitted and documented here rather than faked as Average.
public record DurationStatsDto(int SampleCount, double? AverageHours, double? MinHours, double? MaxHours);

// Metric: Process Volume Trend.
// Source: ProcessInstance. Time field: StartedAt (bucketed) for Started; CompletedAt (bucketed,
// only when Status == Completed) for Completed; CompletedAt (bucketed, only when Status ==
// Rejected) for Rejected — three independently-bucketed counts sharing one time axis, never
// conflating "started" with "completed" (Part 9: "do not call CreatedAt 'StartedAt' unless the
// domain semantics actually equate them" — applied here to keep Started/Completed/Rejected
// genuinely distinct rather than reusing one bucket for all three).
public record VolumeTrendPointDto(DateTime BucketStart, int Started, int Completed, int Rejected);

// Metric: Task Throughput Trend.
// Source: TaskInstance. Time field: CreatedAt (bucketed) for Created; CompletedAt (bucketed, only
// when Status == Completed) for Completed. TaskInstance.StartedAt is inspected and confirmed
// NEVER populated anywhere in the engine (grepped every assignment site — only CompletedAt is
// ever set); CreatedAt is used as the reliable "entered the system" timestamp instead, and is
// never mislabeled as "Started."
public record TaskThroughputPointDto(DateTime BucketStart, int Created, int Completed);

// Metric: Node / Workflow Step Analytics.
// Source: TaskInstance, grouped by (ProcessDefinitionId, NodeId) — NodeId alone is not globally
// unique (it is a string scoped to one ProcessVersion's own DefinitionJson), so grouping by NodeId
// alone across different process definitions would silently conflate unrelated steps; grouping by
// the pair avoids that. NodeName is taken from TaskInstance's own snapshot (set once at task
// creation from whichever ProcessVersion was current then — never re-resolved against the latest
// version, satisfying Part 15/33's historical-ProcessVersion requirement automatically, since
// TaskInstance.NodeName already is a per-instance historical snapshot, not a live lookup).
// Duration uses the same CreatedAt -> CompletedAt basis as Task Duration. OverdueCount is the
// count of this node's TaskSla rows with a non-null OverdueAt (breached at some point — the same
// signal ReportSlaSummaryDto.CompletedBreachedSla uses, applied here without restricting to only
// Completed rows, since a node can be a "bottleneck candidate" due to currently-Overdue tasks too).
public record NodeAnalyticsItemDto(
    string NodeId,
    string NodeName,
    int Executions,
    int Completed,
    DurationStatsDto Duration,
    int OverdueCount);

// Metric: SLA Compliance Trend.
// Source: TaskSla, restricted to rows with Status == Completed (Part 19 — an Active/Warning/
// Overdue task is never counted as compliant just because it hasn't breached yet; only a
// completed row has a determinable compliant/breached outcome). Time field: CompletedAt
// (bucketed) — the trend answers "of the SLA tasks that finished in this period, how many were
// compliant," not "of the tasks that started in this period." CompliantCount = OverdueAt == null;
// BreachedCount = OverdueAt != null (identical definition to ReportSlaSummaryDto's
// CompletedWithinSla/CompletedBreachedSla — Part 19's own explicit instruction to reuse the
// established Reporting definition rather than inventing a new one). ComplianceRate =
// CompliantCount / CompletedSlaTasks, null when CompletedSlaTasks == 0 (never NaN/Infinity).
public record SlaTrendPointDto(DateTime BucketStart, int CompletedSlaTasks, int CompliantCount, int BreachedCount, double? ComplianceRate);

// Metric: Process Comparison (by Process Definition).
// Same authorized+filtered scope as every other metric on this response. SlaComplianceRate and
// OverdueCount are computed from this definition's own TaskSla population, using the identical
// Completed-only/OverdueAt-based definition as SlaTrendPointDto above — one compliance definition,
// reused, never a second one for the comparison view (Part 19's own warning against silently
// maintaining two definitions).
public record ProcessComparisonItemDto(
    Guid ProcessDefinitionId,
    string ProcessDefinitionKey,
    string ProcessDefinitionName,
    int Total,
    int Completed,
    int Rejected,
    DurationStatsDto Duration,
    double? SlaComplianceRate,
    int OverdueCount);

// The one Analytics response — a single GET returns every related aggregate together (Part 27:
// "if one response can safely contain multiple related aggregates, that is acceptable... avoid
// endpoint explosion"), mirroring Dashboard's own established "one GET, one composite DTO"
// precedent. ProcessVolume/TaskThroughput/SlaTrend share one bucketed time axis
// (AnalyticsQuery.Granularity); NodeAnalytics/ProcessComparison are unbucketed aggregate lists.
public record AnalyticsOverviewResponse(
    int TotalProcesses,
    int RunningProcesses,
    int CompletedProcesses,
    int RejectedProcesses,
    IReadOnlyList<VolumeTrendPointDto> VolumeTrend,
    DurationStatsDto ProcessDuration,
    IReadOnlyList<TaskThroughputPointDto> TaskThroughputTrend,
    DurationStatsDto TaskDuration,
    IReadOnlyList<NodeAnalyticsItemDto> NodeAnalytics,
    IReadOnlyList<SlaTrendPointDto> SlaTrend,
    IReadOnlyList<ProcessComparisonItemDto> ProcessComparison);

public interface IAnalyticsQueryService
{
    Task<AnalyticsOverviewResponse> GetOverviewAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, AnalyticsQuery query, CancellationToken cancellationToken = default);
}
