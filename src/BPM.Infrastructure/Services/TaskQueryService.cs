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

    public async Task<IReadOnlyList<TaskDto>> GetMyTasksAsync(Guid userId, IReadOnlyCollection<string> userRoles, CancellationToken cancellationToken = default)
    {
        var plainTaskIds = await _db.TaskInstances
            .Where(t => t.AssigneeId == userId
                || (t.Status == TaskInstanceStatus.Pending && t.AssigneeRole != null && userRoles.Contains(t.AssigneeRole)))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        // ApprovalTask visibility: any task where the caller holds (or was delegated) an
        // approval slot, regardless of that slot's status — mirrors the direct AssigneeId branch
        // above, which shows history too, not just currently-actionable items.
        var approvalTaskIds = await _db.ApprovalAssignments
            .Where(a => a.UserId == userId || a.DelegatedToUserId == userId)
            .Select(a => a.ApprovalInstance!.TaskInstanceId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var taskIds = plainTaskIds.Union(approvalTaskIds).ToList();
        return await LoadTasksAsync(taskIds, cancellationToken);
    }

    public async Task<TaskDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tasks = await LoadTasksAsync(new[] { id }, cancellationToken);
        return tasks.SingleOrDefault();
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

        var approvalsByTaskId = await _db.ApprovalInstances
            .AsNoTracking()
            .Include(a => a.Assignments)
            .Where(a => taskIds.Contains(a.TaskInstanceId))
            .ToDictionaryAsync(a => a.TaskInstanceId, cancellationToken);

        return tasks
            .Select(t => TaskDtoMapper.BuildDto(t, approvalsByTaskId.GetValueOrDefault(t.Id)))
            .ToList();
    }
}
