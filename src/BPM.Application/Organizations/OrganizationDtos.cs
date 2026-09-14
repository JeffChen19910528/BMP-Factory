namespace BPM.Application.Organizations;

public record OrganizationDto(Guid Id, string Name, Guid? ParentId);

public record CreateOrganizationRequest(string Name, Guid? ParentId);

public interface IOrganizationService
{
    Task<IReadOnlyList<OrganizationDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<OrganizationDto> CreateAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default);
}
