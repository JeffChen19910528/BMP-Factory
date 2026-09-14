using BPM.Application.Common;
using BPM.Application.Organizations;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class OrganizationService : IOrganizationService
{
    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;

    public OrganizationService(BpmDbContext db, IAuditService auditService)
    {
        _db = db;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<OrganizationDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.Organizations
            .AsNoTracking()
            .Select(o => new OrganizationDto(o.Id, o.Name, o.ParentId))
            .ToListAsync(cancellationToken);

    public async Task<OrganizationDto> CreateAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default)
    {
        var org = new Organization { Name = request.Name, ParentId = request.ParentId };
        _db.Organizations.Add(org);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("CreateOrganization", nameof(Organization), org.Id.ToString(), newValue: new { org.Name }, cancellationToken: cancellationToken);

        return new OrganizationDto(org.Id, org.Name, org.ParentId);
    }
}
