using BPM.Application.Common;
using BPM.Application.Departments;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class DepartmentService : IDepartmentService
{
    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;
    private readonly IValidator<CreateDepartmentRequest> _createValidator;
    private readonly IValidator<UpdateDepartmentRequest> _updateValidator;

    public DepartmentService(
        BpmDbContext db,
        IAuditService auditService,
        IValidator<CreateDepartmentRequest> createValidator,
        IValidator<UpdateDepartmentRequest> updateValidator)
    {
        _db = db;
        _auditService = auditService;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<IReadOnlyList<DepartmentDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.Departments
            .AsNoTracking()
            .Select(d => ToDto(d))
            .ToListAsync(cancellationToken);

    public async Task<DepartmentDto> CreateAsync(CreateDepartmentRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        // Phase 5.5.2: previously unchecked — two departments with the same name in the same
        // organization was silently allowed (no unique index, no application check).
        var nameTaken = await _db.Departments.AnyAsync(d => d.OrganizationId == request.OrganizationId && d.Name == request.Name, cancellationToken);
        if (nameTaken)
        {
            throw new ConflictAppException("DEPARTMENT_NAME_TAKEN", $"A department named '{request.Name}' already exists in this organization.");
        }

        if (request.ParentId is Guid parentId)
        {
            var parentExists = await _db.Departments.AnyAsync(d => d.Id == parentId, cancellationToken);
            if (!parentExists)
            {
                throw new BadRequestAppException("INVALID_PARENT_DEPARTMENT", $"Parent department '{parentId}' was not found.");
            }
        }

        if (request.ManagerUserId is Guid managerId)
        {
            await EnsureManagerIsValidAsync(managerId, cancellationToken);
        }

        var department = new Department
        {
            Name = request.Name,
            OrganizationId = request.OrganizationId,
            ParentId = request.ParentId,
            ManagerUserId = request.ManagerUserId,
        };
        _db.Departments.Add(department);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.CreateDepartment, nameof(Department), department.Id.ToString(), newValue: new { department.Name }, cancellationToken: cancellationToken);

        return ToDto(department);
    }

    public async Task<DepartmentDto?> UpdateAsync(Guid id, UpdateDepartmentRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var department = await _db.Departments.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (department is null)
        {
            return null;
        }

        var nameTakenByAnother = await _db.Departments
            .AnyAsync(d => d.Id != id && d.OrganizationId == department.OrganizationId && d.Name == request.Name, cancellationToken);
        if (nameTakenByAnother)
        {
            throw new ConflictAppException("DEPARTMENT_NAME_TAKEN", $"A department named '{request.Name}' already exists in this organization.");
        }

        if (request.ParentId is Guid parentId)
        {
            // Self-parent: a department cannot be its own parent.
            if (parentId == id)
            {
                throw new BadRequestAppException("INVALID_PARENT_DEPARTMENT", "A department cannot be its own parent.");
            }

            var parent = await _db.Departments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == parentId, cancellationToken);
            if (parent is null)
            {
                throw new BadRequestAppException("INVALID_PARENT_DEPARTMENT", $"Parent department '{parentId}' was not found.");
            }

            // Circular parent: walk up the proposed parent's own ancestor chain — if `id` appears
            // anywhere in it, this update would create a cycle (id would become an ancestor of
            // its own ancestor). Bounded by the tree's actual depth, never unbounded recursion —
            // a corrupt/cyclic chain already in the database (shouldn't happen, but defensively)
            // is guarded by a hard iteration cap rather than looping forever.
            var ancestorId = parent.ParentId;
            var guard = 0;
            while (ancestorId is not null && guard++ < 1000)
            {
                if (ancestorId == id)
                {
                    throw new BadRequestAppException("CIRCULAR_DEPARTMENT_HIERARCHY", "This change would create a circular department hierarchy.");
                }
                ancestorId = await _db.Departments.AsNoTracking().Where(d => d.Id == ancestorId).Select(d => d.ParentId).SingleOrDefaultAsync(cancellationToken);
            }
        }

        if (request.ManagerUserId is Guid managerId)
        {
            await EnsureManagerIsValidAsync(managerId, cancellationToken);
        }

        var oldValue = new { department.Name, department.ParentId, department.ManagerUserId };

        department.Name = request.Name;
        department.ParentId = request.ParentId;
        department.ManagerUserId = request.ManagerUserId;

        byte[] expectedVersion;
        try
        {
            expectedVersion = Convert.FromBase64String(request.ExpectedVersion);
        }
        catch (FormatException)
        {
            throw new BadRequestAppException("INVALID_EXPECTED_VERSION", "ExpectedVersion must be a base64-encoded RowVersion.");
        }
        _db.Entry(department).Property(d => d.RowVersion).OriginalValue = expectedVersion;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("DEPARTMENT_CONCURRENCY_CONFLICT", "This department was modified by another administrator. Reload and retry.");
        }

        await _auditService.LogAsync(
            "UpdateDepartment",
            nameof(Department),
            department.Id.ToString(),
            oldValue,
            new { department.Name, department.ParentId, department.ManagerUserId },
            cancellationToken);

        return ToDto(department);
    }

    // Phase 5.5.2 §10: ManagerUserId was accepted on both Create and Update but never validated —
    // a nonexistent or disabled user id was silently persisted. Mirrors the existing ParentId
    // existence-check pattern above; the same rule applies on both Create and Update since a
    // manager can be set at either point.
    private async Task EnsureManagerIsValidAsync(Guid managerId, CancellationToken cancellationToken)
    {
        var manager = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == managerId, cancellationToken);
        if (manager is null)
        {
            throw new BadRequestAppException("INVALID_MANAGER", $"Manager user '{managerId}' was not found.");
        }
        if (!manager.IsActive)
        {
            throw new BadRequestAppException("INVALID_MANAGER", "Manager user is not active.");
        }
    }

    private static DepartmentDto ToDto(Department d) =>
        new(d.Id, d.Name, d.OrganizationId, d.ParentId, d.ManagerUserId, Convert.ToBase64String(d.RowVersion));
}
