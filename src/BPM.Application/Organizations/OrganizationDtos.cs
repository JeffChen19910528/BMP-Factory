namespace BPM.Application.Organizations;

// Phase 9 — RowVersion added, same base64 RowVersion/ExpectedVersion convention as every other
// AuditableEntity DTO in this codebase (User/Department/ProcessVersion/FormVersion) — Organization
// already had the RowVersion column (Phase 1, via AuditableEntity) but nothing surfaced it until
// now, since there was no Update endpoint to need it.
public record OrganizationDto(Guid Id, string Name, Guid? ParentId, string RowVersion);

public record CreateOrganizationRequest(string Name, Guid? ParentId);

// Phase 9 — mirrors UpdateDepartmentRequest's own shape exactly (Name + ParentId + ExpectedVersion)
// — Organization has no other mutable fields (no ManagerUserId, unlike Department).
public record UpdateOrganizationRequest(string Name, Guid? ParentId, string ExpectedVersion);

public interface IOrganizationService
{
    Task<IReadOnlyList<OrganizationDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<OrganizationDto> CreateAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default);

    // Phase 9 — returns null if the organization doesn't exist, matching UpdateDepartmentAsync's
    // own not-found convention (a 404 from the controller, not an exception).
    Task<OrganizationDto?> UpdateAsync(Guid id, UpdateOrganizationRequest request, CancellationToken cancellationToken = default);
}
