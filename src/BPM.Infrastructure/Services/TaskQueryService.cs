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

    public async Task<IReadOnlyList<TaskDto>> GetMyTasksAsync(Guid userId, IReadOnlyCollection<string> userRoles, CancellationToken cancellationToken = default) =>
        await _db.TaskInstances
            .AsNoTracking()
            .Where(t => t.AssigneeId == userId
                || (t.Status == TaskInstanceStatus.Pending && t.AssigneeRole != null && userRoles.Contains(t.AssigneeRole)))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TaskDto(t.Id, t.ProcessInstanceId, t.NodeId, t.NodeName, t.AssigneeId, t.AssigneeRole, t.Status, t.CreatedAt, t.StartedAt, t.CompletedAt, t.DueAt))
            .ToListAsync(cancellationToken);

    public async Task<TaskDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _db.TaskInstances
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new TaskDto(t.Id, t.ProcessInstanceId, t.NodeId, t.NodeName, t.AssigneeId, t.AssigneeRole, t.Status, t.CreatedAt, t.StartedAt, t.CompletedAt, t.DueAt))
            .SingleOrDefaultAsync(cancellationToken);
}
