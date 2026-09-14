namespace BPM.Application.Departments;

public record DepartmentDto(Guid Id, string Name, Guid OrganizationId, Guid? ParentId, Guid? ManagerUserId);

public record CreateDepartmentRequest(string Name, Guid OrganizationId, Guid? ParentId, Guid? ManagerUserId);

public interface IDepartmentService
{
    Task<IReadOnlyList<DepartmentDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DepartmentDto> CreateAsync(CreateDepartmentRequest request, CancellationToken cancellationToken = default);
}
