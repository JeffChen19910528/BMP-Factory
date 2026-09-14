namespace BPM.Application.Roles;

public record RoleDto(Guid Id, string Name);

public record CreateRoleRequest(string Name);

public record AssignRoleRequest(Guid UserId, Guid RoleId);

public interface IRoleService
{
    Task<IReadOnlyList<RoleDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<RoleDto> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken = default);
    Task AssignAsync(AssignRoleRequest request, CancellationToken cancellationToken = default);
}
