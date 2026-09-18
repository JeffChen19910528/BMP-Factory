namespace BPM.Application.Departments;

// Phase 5.5.2: RowVersion added, same reasoning/pattern as UserDto (Department already extended
// AuditableEntity and already had a configured RowVersion concurrency token; nothing surfaced it
// before this phase because no Update endpoint existed at all).
public record DepartmentDto(Guid Id, string Name, Guid OrganizationId, Guid? ParentId, Guid? ManagerUserId, string RowVersion);

public record CreateDepartmentRequest(string Name, Guid OrganizationId, Guid? ParentId, Guid? ManagerUserId);

// Phase 5.5.2: Department had no Update at all before this phase (Create + GetAll only) — the
// Administration Departments workspace's own "edit" requirement is what's genuinely new here.
public record UpdateDepartmentRequest(string Name, Guid? ParentId, Guid? ManagerUserId, string ExpectedVersion);

public interface IDepartmentService
{
    Task<IReadOnlyList<DepartmentDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<DepartmentDto> CreateAsync(CreateDepartmentRequest request, CancellationToken cancellationToken = default);
    Task<DepartmentDto?> UpdateAsync(Guid id, UpdateDepartmentRequest request, CancellationToken cancellationToken = default);
}
