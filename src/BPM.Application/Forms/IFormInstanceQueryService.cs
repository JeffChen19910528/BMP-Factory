namespace BPM.Application.Forms;

public interface IFormInstanceQueryService
{
    // Throws ForbiddenAppException if currentUserId isn't authorized to view this instance
    // (Skill.md §21/§22: creator, the linked task's assignee, any approver on the same
    // ProcessInstance, or an Administrator).
    Task<FormInstanceDto?> GetByIdAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    Task<FormDataDto?> GetDataAsync(Guid formInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FormInstanceDto>> GetByProcessInstanceAsync(Guid processInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);
}
