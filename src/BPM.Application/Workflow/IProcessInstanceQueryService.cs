namespace BPM.Application.Workflow;

public interface IProcessInstanceQueryService
{
    Task<IReadOnlyList<ProcessInstanceDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ProcessInstanceDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ITaskQueryService
{
    // Tasks the given user can act on right now: assigned to them directly, or open (Pending)
    // and assigned to a role they hold.
    Task<IReadOnlyList<TaskDto>> GetMyTasksAsync(Guid userId, IReadOnlyCollection<string> userRoles, CancellationToken cancellationToken = default);
    Task<TaskDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
