using BPM.Application.Common;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class TaskQueryService : ITaskQueryService
{
    private readonly BpmDbContext _db;

    public TaskQueryService(BpmDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<TaskDto>> GetMyTasksAsync(Guid userId, IReadOnlyCollection<string> userRoles, MyTasksQuery query, CancellationToken cancellationToken = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 200 ? 200 : query.PageSize;
        // Phase 12 — same overflow guard Phase 7.2.3 established in ProcessMonitoringQueryService.
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

        var taskIdsQuery = BuildMyTaskIdsQuery(_db, userId, userRoles);
        var totalCount = await taskIdsQuery.CountAsync(cancellationToken);

        // Ordering + paging pushed to the database (ORDER BY CreatedAt DESC, then Skip/Take
        // against TaskInstances directly), never a full unbounded id-list fetch followed by an
        // in-memory slice — the same discipline every other paginated query in this codebase uses.
        var pageTasks = await _db.TaskInstances
            .AsNoTracking()
            .Where(t => taskIdsQuery.Contains(t.Id))
            .OrderByDescending(t => t.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = await BuildDtosAsync(pageTasks, cancellationToken);

        return new PagedResult<TaskDto>(items, totalCount, page, pageSize);
    }

    // Phase 7.1 — extracted verbatim from GetMyTasksAsync's own two subqueries (unioned) so
    // DashboardQueryService can reuse the *exact* same "which tasks can this user see" computation
    // for aggregate counting, without duplicating the authorization predicate a second time (Part
    // 1/30: reuse existing query-service authorization semantics rather than reinventing them).
    // Returns an unexecuted IQueryable so callers can compose further server-side filtering (e.g.
    // joining to TaskSlas) instead of materializing the full task list first.
    internal static IQueryable<Guid> BuildMyTaskIdsQuery(BpmDbContext db, Guid userId, IReadOnlyCollection<string> userRoles)
    {
        var plainTaskIds = db.TaskInstances
            .Where(t => t.AssigneeId == userId
                || (t.Status == TaskInstanceStatus.Pending && t.AssigneeRole != null && userRoles.Contains(t.AssigneeRole)))
            .Select(t => t.Id);

        // ApprovalTask visibility: any task where the caller holds (or was delegated) an
        // approval slot, regardless of that slot's status — mirrors the direct AssigneeId branch
        // above, which shows history too, not just currently-actionable items.
        var approvalTaskIds = db.ApprovalAssignments
            .Where(a => a.UserId == userId || a.DelegatedToUserId == userId)
            .Select(a => a.ApprovalInstance!.TaskInstanceId);

        return plainTaskIds.Union(approvalTaskIds);
    }

    private const string AdministratorRole = "Administrator";

    public async Task<TaskDto?> GetByIdAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var tasks = await LoadTasksAsync(new[] { id }, cancellationToken);
        var task = tasks.SingleOrDefault();
        if (task is null)
        {
            return null;
        }

        if (currentUserRoles.Contains(AdministratorRole)
            || task.AssigneeId == currentUserId
            || (task.AssigneeRole is not null && currentUserRoles.Contains(task.AssigneeRole)))
        {
            return task;
        }

        var isApprovalParticipant = await _db.ApprovalAssignments
            .AnyAsync(a => a.ApprovalInstance!.TaskInstanceId == id
                && (a.UserId == currentUserId || a.DelegatedToUserId == currentUserId), cancellationToken);
        if (isApprovalParticipant)
        {
            return task;
        }

        throw new ForbiddenAppException("TASK_NOT_AUTHORIZED", "You are not authorized to view this task.");
    }

    // Phase 5.5.1 — Approvals Worklist. Scoped to the exact same approval-participant computation
    // GetMyTasksAsync's approvalTaskIds subquery already performs (direct or delegated candidate
    // on an ApprovalAssignment) — reused as a subquery here, not copied, so the two can never
    // silently diverge on "which approvals can this user see." currentUserId is always the
    // server-derived authenticated caller (see ITaskQueryService's doc comment) — there is no
    // "pass any user id" path into this method.
    public async Task<PagedResult<ApprovalWorklistItemDto>> GetApprovalWorklistAsync(Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ApprovalWorklistQuery query, CancellationToken cancellationToken = default)
    {
        var approvalTaskIds = _db.ApprovalAssignments
            .Where(a => a.UserId == currentUserId || a.DelegatedToUserId == currentUserId)
            .Select(a => a.ApprovalInstance!.TaskInstanceId)
            .Distinct();

        var q =
            from t in _db.TaskInstances.AsNoTracking()
            where approvalTaskIds.Contains(t.Id)
            join pi in _db.ProcessInstances.AsNoTracking() on t.ProcessInstanceId equals pi.Id
            join pd in _db.ProcessDefinitions.AsNoTracking() on pi.ProcessDefinitionId equals pd.Id
            select new { Task = t, ProcessInstance = pi, ProcessDefinition = pd };

        if (query.Status is not null)
        {
            q = q.Where(x => x.Task.Status == query.Status);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            q = q.Where(x => EF.Functions.ILike(x.ProcessDefinition.Name, $"%{search}%")
                || EF.Functions.ILike(x.ProcessDefinition.Key, $"%{search}%")
                || EF.Functions.ILike(x.Task.NodeName, $"%{search}%"));
        }

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 200 ? 20 : query.PageSize;

        // Phase 12 — same overflow guard Phase 7.2.3 established in ProcessMonitoringQueryService.
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

        var totalCount = await q.CountAsync(cancellationToken);
        var pageRows = await q
            .OrderByDescending(x => x.Task.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // One bounded (page-sized, not table-sized) follow-up query for approval progress —
        // matches LoadTasksAsync's existing pattern exactly, avoiding a per-row query (§33).
        var taskIds = pageRows.Select(x => x.Task.Id).ToList();
        var approvalsByTaskId = await _db.ApprovalInstances
            .AsNoTracking()
            .Include(a => a.Assignments)
            .Where(a => taskIds.Contains(a.TaskInstanceId))
            .ToDictionaryAsync(a => a.TaskInstanceId, cancellationToken);

        var items = pageRows
            .Select(x => new ApprovalWorklistItemDto(
                x.Task.Id,
                x.Task.ProcessInstanceId,
                x.ProcessDefinition.Key,
                x.ProcessDefinition.Name,
                x.Task.NodeName,
                x.ProcessInstance.InitiatorId,
                x.Task.AssigneeId,
                x.Task.AssigneeRole,
                x.Task.Status,
                x.Task.CreatedAt,
                x.Task.CompletedAt ?? x.Task.StartedAt ?? x.Task.CreatedAt,
                x.Task.DueAt,
                TaskDtoMapper.BuildApprovalSummary(approvalsByTaskId.GetValueOrDefault(x.Task.Id))))
            .ToList();

        return new PagedResult<ApprovalWorklistItemDto>(items, totalCount, page, pageSize);
    }

    private async Task<IReadOnlyList<TaskDto>> LoadTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0)
        {
            return Array.Empty<TaskDto>();
        }

        var tasks = await _db.TaskInstances
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        return await BuildDtosAsync(tasks, cancellationToken);
    }

    // Phase 12 — extracted from LoadTasksAsync so GetMyTasksAsync's now-paginated query (which
    // fetches its own page of TaskInstance entities directly, rather than an id list handed to
    // LoadTasksAsync) can reuse the exact same bounded (page-sized, not table-sized) Approvals/SLA
    // follow-up-query shape without duplicating it a second time.
    private async Task<IReadOnlyList<TaskDto>> BuildDtosAsync(IReadOnlyCollection<TaskInstance> tasks, CancellationToken cancellationToken)
    {
        if (tasks.Count == 0)
        {
            return Array.Empty<TaskDto>();
        }

        var taskIds = tasks.Select(t => t.Id).ToList();

        var approvalsByTaskId = await _db.ApprovalInstances
            .AsNoTracking()
            .Include(a => a.Assignments)
            .Where(a => taskIds.Contains(a.TaskInstanceId))
            .ToDictionaryAsync(a => a.TaskInstanceId, cancellationToken);

        // Phase 6.3: same bounded (page-sized, not table-sized) follow-up query shape as
        // approvalsByTaskId above — never a per-row query.
        var slasByTaskId = await _db.TaskSlas
            .AsNoTracking()
            .Where(s => taskIds.Contains(s.TaskInstanceId))
            .ToDictionaryAsync(s => s.TaskInstanceId, cancellationToken);

        return tasks
            .Select(t => TaskDtoMapper.BuildDto(t, approvalsByTaskId.GetValueOrDefault(t.Id), slasByTaskId.GetValueOrDefault(t.Id)))
            .ToList();
    }
}
