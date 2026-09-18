using BPM.Application.Common;
using BPM.Application.ProcessMonitoring;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Phase 7.2.1 — Process Monitoring, read-only. Never modifies ProcessInstance/TaskInstance/
// ApprovalAssignment/TaskSla. Reuses ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync
// verbatim for authorization — no second "which process instances can this user see"
// implementation (Part 3/30's explicit warning). Authorization is always applied as its own
// unconditional filter on the base query, *before* any user-supplied filter is layered on top —
// every ProcessMonitoringQuery filter (Status/ProcessDefinitionId/InitiatorId/SlaStatus/Search/
// date range) can only narrow that already-authorized set further, never substitute for it or
// widen it (Part 16).
//
// Query strategy (Part 26 — "a few bounded queries... clearer and still performant, that is
// acceptable"): one composed query handles authorization + filtering + sorting + counting +
// pagination (it needs the active task's SLA state to filter/sort by SlaStatus/SlaDueAt *before*
// pagination, so that correlated data must be part of this query, not deferred) — then two small,
// page-bounded follow-up queries (initiator display names, approval-candidate info) resolve
// display-only data for just the returned page, mirroring the same "bounded follow-up query"
// pattern TaskQueryService.LoadTasksAsync/GetApprovalWorklistAsync and DashboardQueryService's
// RecentActivityAsync already established — never a per-row query, never N+1.
public class ProcessMonitoringQueryService : IProcessMonitoringQueryService
{
    private const string AdministratorRole = "Administrator";

    private readonly BpmDbContext _db;

    public ProcessMonitoringQueryService(BpmDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<ProcessMonitoringItemDto>> GetAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ProcessMonitoringQuery query, CancellationToken cancellationToken = default)
    {
        var isAdministrator = currentUserRoles.Contains(AdministratorRole);

        var authorizedBase = _db.ProcessInstances.AsNoTracking().AsQueryable();
        if (!isAdministrator)
        {
            // Reused verbatim — the exact same authorization computation
            // IProcessInstanceQueryService.GetAllAsync itself uses for a normal user.
            var visibleIds = await ProcessInstanceQueryService.VisibleProcessInstanceIdsAsync(_db, currentUserId, currentUserRoles, cancellationToken);
            authorizedBase = authorizedBase.Where(p => visibleIds.Contains(p.Id));
        }

        // Active task = the engine's current unit of work for this process. Because
        // ParallelGateway/JoinGateway are reserved, never-executable node types (confirmed by
        // inspection — see WorkflowDefinitionValidator's UNSUPPORTED_NODE_TYPE check), a
        // ProcessInstance has at most one non-terminal TaskInstance at any time, so a single
        // FirstOrDefault correlated subquery is correct today, not an approximation.
        var activeTaskCandidates = _db.TaskInstances.Where(t => t.Status == TaskInstanceStatus.Pending || t.Status == TaskInstanceStatus.InProgress);

        // Real LEFT JOINs throughout, deliberately not `let`-bound correlated subqueries — a
        // correlated FirstOrDefault() referenced from multiple places (a filter, a sort key, and
        // the final projection) does not translate reliably once EF has to re-inline it at each
        // reference (confirmed by an actual translation failure hit while implementing this
        // query — see PROGRESS.md's Phase 7.2.1 section). Chained LEFT JOINs are the
        // well-supported, standard EF Core pattern for "at most one optionally-related row" and
        // translate to a single flat SQL query.
        var composed =
            from p in authorizedBase
            join pd in _db.ProcessDefinitions.AsNoTracking() on p.ProcessDefinitionId equals pd.Id
            join iu in _db.Users.AsNoTracking() on p.InitiatorId equals iu.Id into initiatorJoin
            from initiator in initiatorJoin.DefaultIfEmpty()
            join at in activeTaskCandidates on p.Id equals at.ProcessInstanceId into activeTaskJoin
            from activeTask in activeTaskJoin.DefaultIfEmpty()
            join s in _db.TaskSlas on activeTask.Id equals s.TaskInstanceId into slaJoin
            from sla in slaJoin.DefaultIfEmpty()
            select new
            {
                Process = p,
                Definition = pd,
                Initiator = initiator,
                ActiveTask = activeTask,
                Sla = sla,
                // The default/most-common sort key. Sequential engine (see above): the currently
                // active task's own CreatedAt *is* the moment of the most recent real transition
                // into the process's current state — no MAX-over-all-tasks aggregation needed.
                // A process with no active task has already reached a terminal state, at which
                // point CompletedAt is authoritative.
                UpdatedAt = p.CompletedAt ?? (activeTask != null ? activeTask.CreatedAt : p.StartedAt),
            };

        if (query.Status is not null)
        {
            composed = composed.Where(x => x.Process.Status == query.Status);
        }
        if (query.ProcessDefinitionId is not null)
        {
            composed = composed.Where(x => x.Process.ProcessDefinitionId == query.ProcessDefinitionId);
        }
        if (query.InitiatorId is not null)
        {
            // Narrows only — this can never *broaden* visibility beyond what the authorization
            // filter above already allows, since it is ANDed onto that already-restricted query,
            // never a replacement for it (Part 15/16: "do not trust filter parameters for access
            // control" — this one never functions as one).
            composed = composed.Where(x => x.Process.InitiatorId == query.InitiatorId);
        }
        if (query.DepartmentId is not null)
        {
            // Same narrow-only guarantee as InitiatorId above — ANDed onto the already-authorized
            // query, never a substitute for it.
            composed = composed.Where(x => x.Initiator != null && x.Initiator.DepartmentId == query.DepartmentId);
        }
        if (query.StartedFrom is not null)
        {
            composed = composed.Where(x => x.Process.StartedAt >= query.StartedFrom);
        }
        if (query.StartedTo is not null)
        {
            composed = composed.Where(x => x.Process.StartedAt <= query.StartedTo);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            composed = composed.Where(x =>
                EF.Functions.ILike(x.Definition.Name, $"%{search}%")
                || EF.Functions.ILike(x.Definition.Key, $"%{search}%")
                || (x.Initiator != null && EF.Functions.ILike(x.Initiator.DisplayName, $"%{search}%")));
        }
        if (query.SlaStatus is not null)
        {
            composed = query.SlaStatus.Value switch
            {
                ProcessMonitoringSlaStatus.Active => composed.Where(x => x.Sla != null && x.Sla.Status == TaskSlaStatus.Active && x.Sla.WarningNotifiedAt == null),
                ProcessMonitoringSlaStatus.Warning => composed.Where(x => x.Sla != null && x.Sla.Status == TaskSlaStatus.Active && x.Sla.WarningNotifiedAt != null),
                ProcessMonitoringSlaStatus.Overdue => composed.Where(x => x.Sla != null && x.Sla.Status == TaskSlaStatus.Overdue),
                ProcessMonitoringSlaStatus.Completed => composed.Where(x => x.Sla != null && x.Sla.Status == TaskSlaStatus.Completed),
                _ => composed,
            };
        }

        var totalCount = await composed.CountAsync(cancellationToken);

        var page = query.Page < 1 ? 1 : query.Page;
        // Upper bound raised from 200 to 10001 in Phase 7.3 so ReportQueryService.ExportCsvAsync
        // can request ReportExportLimits.MaxExportRows + 1 rows through this exact same method
        // (Part 22: export must reuse the identical query, not a parallel "admin export query").
        // The UI default stays 20; nothing about normal Process Monitoring/Reporting page browsing
        // changes.
        var pageSize = query.PageSize is < 1 or > 10001 ? 20 : query.PageSize;

        // Phase 7.2.3 hardening — a genuine bug found and fixed: `(page - 1) * pageSize` is plain
        // `int` arithmetic with no upper clamp on `page`, so a sufficiently large `Page` (e.g.
        // `int.MaxValue`) overflows and wraps to a negative value. `Skip()` then produced a
        // negative SQL OFFSET, which PostgreSQL rejects with `PostgresException: OFFSET must not
        // be negative` — an unhandled exception ErrorHandlingMiddleware does not catch (it only
        // maps AppException/ValidationException), so it fell through to ASP.NET's default
        // exception handling and could leak the raw SQL error/stack trace to the client. Fixed by
        // computing the skip in `long` arithmetic and clamping to `int.MaxValue` — a page that far
        // out will always yield zero rows in any real dataset anyway, so clamping is both safe and
        // semantically correct (an empty page, not an error).
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

        // Whitelisted sort mapping (Part 13) — never a raw client-supplied order expression.
        // A stable secondary key (Process.Id) guarantees deterministic pagination even when the
        // primary sort key ties (Part 34's "deterministic ordering when timestamps are equal").
        var descending = query.SortDirection == SortDirection.Descending;
        composed = query.SortBy switch
        {
            ProcessMonitoringSortBy.StartedAt => descending ? composed.OrderByDescending(x => x.Process.StartedAt).ThenBy(x => x.Process.Id) : composed.OrderBy(x => x.Process.StartedAt).ThenBy(x => x.Process.Id),
            ProcessMonitoringSortBy.Status => descending ? composed.OrderByDescending(x => x.Process.Status).ThenBy(x => x.Process.Id) : composed.OrderBy(x => x.Process.Status).ThenBy(x => x.Process.Id),
            ProcessMonitoringSortBy.ProcessDefinitionName => descending ? composed.OrderByDescending(x => x.Definition.Name).ThenBy(x => x.Process.Id) : composed.OrderBy(x => x.Definition.Name).ThenBy(x => x.Process.Id),
            ProcessMonitoringSortBy.SlaDueAt => descending ? composed.OrderByDescending(x => x.Sla != null ? x.Sla.DueAt : (DateTime?)null).ThenBy(x => x.Process.Id) : composed.OrderBy(x => x.Sla != null ? x.Sla.DueAt : (DateTime?)null).ThenBy(x => x.Process.Id),
            _ => descending ? composed.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Process.Id) : composed.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Process.Id),
        };

        var pageRows = await composed
            .Skip(skip)
            .Take(pageSize)
            .Select(x => new
            {
                x.Process.Id,
                x.Process.ProcessDefinitionId,
                x.Definition.Key,
                x.Definition.Name,
                x.Process.Status,
                x.Process.InitiatorId,
                x.Process.StartedAt,
                x.UpdatedAt,
                ActiveTaskId = x.ActiveTask != null ? x.ActiveTask.Id : (Guid?)null,
                ActiveTaskName = x.ActiveTask != null ? x.ActiveTask.NodeName : null,
                ActiveTaskStatus = x.ActiveTask != null ? x.ActiveTask.Status : (TaskInstanceStatus?)null,
                ActiveTaskAssigneeId = x.ActiveTask != null ? x.ActiveTask.AssigneeId : null,
                ActiveTaskAssigneeRole = x.ActiveTask != null ? x.ActiveTask.AssigneeRole : null,
                SlaStatusRaw = x.Sla != null ? x.Sla.Status : (TaskSlaStatus?)null,
                SlaWarningNotifiedAt = x.Sla != null ? x.Sla.WarningNotifiedAt : null,
                SlaDueAt = x.Sla != null ? x.Sla.DueAt : (DateTime?)null,
            })
            .ToListAsync(cancellationToken);

        // ---- Page-bounded follow-up resolution (never per-row, never N+1) ----

        var initiatorIds = pageRows.Select(r => r.InitiatorId).Distinct().ToList();
        var initiatorNames = await _db.Users.AsNoTracking()
            .Where(u => initiatorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        var activeApprovalTaskIds = pageRows.Where(r => r.ActiveTaskId is not null && r.ActiveTaskAssigneeId is null && r.ActiveTaskAssigneeRole is null)
            .Select(r => r.ActiveTaskId!.Value).ToList();
        var approvalsByTaskId = await _db.ApprovalInstances.AsNoTracking()
            .Include(a => a.Assignments)
            .Where(a => activeApprovalTaskIds.Contains(a.TaskInstanceId))
            .ToDictionaryAsync(a => a.TaskInstanceId, cancellationToken);

        var items = pageRows.Select(r =>
        {
            string? currentTaskAssigneeDisplay = null;
            var isApprovalTask = false;

            if (r.ActiveTaskId is Guid activeTaskId)
            {
                if (r.ActiveTaskAssigneeId is Guid assigneeId)
                {
                    currentTaskAssigneeDisplay = initiatorNames.TryGetValue(assigneeId, out var name) ? name : assigneeId.ToString();
                }
                else if (r.ActiveTaskAssigneeRole is string role)
                {
                    currentTaskAssigneeDisplay = $"Role: {role}";
                }
                else if (approvalsByTaskId.TryGetValue(activeTaskId, out var approval))
                {
                    isApprovalTask = true;
                    var pendingCount = approval.Assignments.Count(a => a.Status == ApprovalAssignmentStatus.Pending);
                    currentTaskAssigneeDisplay = $"Pending approval ({pendingCount} candidate{(pendingCount == 1 ? "" : "s")})";
                }
            }

            var slaStatus = r.SlaStatusRaw is TaskSlaStatus rawStatus ? ProcessMonitoringSlaStatusMapper.From(rawStatus, r.SlaWarningNotifiedAt) : null;

            return new ProcessMonitoringItemDto(
                ProcessInstanceId: r.Id,
                ProcessDefinitionId: r.ProcessDefinitionId,
                ProcessDefinitionKey: r.Key,
                ProcessDefinitionName: r.Name,
                Status: r.Status,
                InitiatorId: r.InitiatorId,
                InitiatorDisplayName: initiatorNames.TryGetValue(r.InitiatorId, out var initiatorName) ? initiatorName : r.InitiatorId.ToString(),
                StartedAt: r.StartedAt,
                UpdatedAt: r.UpdatedAt,
                CurrentTaskId: r.ActiveTaskId,
                CurrentTaskName: r.ActiveTaskName,
                CurrentTaskStatus: r.ActiveTaskStatus,
                CurrentTaskIsApprovalTask: isApprovalTask,
                CurrentTaskAssigneeDisplay: currentTaskAssigneeDisplay,
                // Always 0 or 1 today (see class comment) — never approximated as always-1 in
                // case a future phase adds real parallel-gateway support.
                ActiveTaskCount: r.ActiveTaskId is not null ? 1 : 0,
                SlaStatus: slaStatus,
                SlaDueAt: r.SlaDueAt);
        }).ToList();

        return new PagedResult<ProcessMonitoringItemDto>(items, totalCount, page, pageSize);
    }
}
