using BPM.Application.Common;
using BPM.Application.Organizations;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class OrganizationService : IOrganizationService
{
    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;
    private readonly IValidator<CreateOrganizationRequest> _createValidator;
    private readonly IValidator<UpdateOrganizationRequest> _updateValidator;

    public OrganizationService(
        BpmDbContext db,
        IAuditService auditService,
        IValidator<CreateOrganizationRequest> createValidator,
        IValidator<UpdateOrganizationRequest> updateValidator)
    {
        _db = db;
        _auditService = auditService;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<IReadOnlyList<OrganizationDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.Organizations
            .AsNoTracking()
            .Select(o => ToDto(o))
            .ToListAsync(cancellationToken);

    public async Task<OrganizationDto> CreateAsync(CreateOrganizationRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        if (request.ParentId is Guid parentId)
        {
            var parentExists = await _db.Organizations.AnyAsync(o => o.Id == parentId, cancellationToken);
            if (!parentExists)
            {
                throw new BadRequestAppException("INVALID_PARENT_ORGANIZATION", $"Parent organization '{parentId}' was not found.");
            }
        }

        var org = new Organization { Name = request.Name, ParentId = request.ParentId };
        _db.Organizations.Add(org);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.CreateOrganization, nameof(Organization), org.Id.ToString(), newValue: new { org.Name, org.ParentId }, cancellationToken: cancellationToken);

        return ToDto(org);
    }

    // Phase 9 Part 6/7/8/9 — mirrors DepartmentService.UpdateAsync exactly: existence check,
    // parent existence + self-parent + circular-hierarchy validation (the identical bounded
    // ancestor-walk pattern, reused rather than a separate hierarchy service), RowVersion/
    // ExpectedVersion optimistic concurrency, and an audit entry — the same shape Department's own
    // Update already established, just without a ManagerUserId or per-organization name-uniqueness
    // rule (Organization never had either).
    public async Task<OrganizationDto?> UpdateAsync(Guid id, UpdateOrganizationRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var organization = await _db.Organizations.SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (organization is null)
        {
            return null;
        }

        if (request.ParentId is Guid parentId)
        {
            if (parentId == id)
            {
                throw new BadRequestAppException("INVALID_PARENT_ORGANIZATION", "An organization cannot be its own parent.");
            }

            var parent = await _db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == parentId, cancellationToken);
            if (parent is null)
            {
                throw new BadRequestAppException("INVALID_PARENT_ORGANIZATION", $"Parent organization '{parentId}' was not found.");
            }

            // Circular parent: identical bounded ancestor-walk DepartmentService.UpdateAsync
            // already uses — walk up the proposed parent's own ancestor chain; if `id` appears
            // anywhere in it, this update would create a cycle.
            var ancestorId = parent.ParentId;
            var guard = 0;
            while (ancestorId is not null && guard++ < 1000)
            {
                if (ancestorId == id)
                {
                    throw new BadRequestAppException("CIRCULAR_ORGANIZATION_HIERARCHY", "This change would create a circular organization hierarchy.");
                }
                ancestorId = await _db.Organizations.AsNoTracking().Where(o => o.Id == ancestorId).Select(o => o.ParentId).SingleOrDefaultAsync(cancellationToken);
            }
        }

        var oldValue = new { organization.Name, organization.ParentId };

        organization.Name = request.Name;
        organization.ParentId = request.ParentId;

        byte[] expectedVersion;
        try
        {
            expectedVersion = Convert.FromBase64String(request.ExpectedVersion);
        }
        catch (FormatException)
        {
            throw new BadRequestAppException("INVALID_EXPECTED_VERSION", "ExpectedVersion must be a base64-encoded RowVersion.");
        }
        _db.Entry(organization).Property(o => o.RowVersion).OriginalValue = expectedVersion;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("ORGANIZATION_CONCURRENCY_CONFLICT", "This organization was modified by another administrator. Reload and retry.");
        }

        await _auditService.LogAsync(
            AuditActions.ModifyOrganization,
            nameof(Organization),
            organization.Id.ToString(),
            oldValue,
            new { organization.Name, organization.ParentId },
            cancellationToken);

        return ToDto(organization);
    }

    private static OrganizationDto ToDto(Organization o) => new(o.Id, o.Name, o.ParentId, Convert.ToBase64String(o.RowVersion));
}
