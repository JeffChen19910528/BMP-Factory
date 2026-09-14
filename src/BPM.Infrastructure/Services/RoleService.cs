using BPM.Application.Common;
using BPM.Application.Roles;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class RoleService : IRoleService
{
    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;

    public RoleService(BpmDbContext db, IAuditService auditService)
    {
        _db = db;
        _auditService = auditService;
    }

    public async Task<IReadOnlyList<RoleDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.Roles
            .AsNoTracking()
            .Select(r => new RoleDto(r.Id, r.Name))
            .ToListAsync(cancellationToken);

    public async Task<RoleDto> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken = default)
    {
        var role = new Role { Name = request.Name };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync("CreateRole", nameof(Role), role.Id.ToString(), newValue: new { role.Name }, cancellationToken: cancellationToken);

        return new RoleDto(role.Id, role.Name);
    }

    public async Task AssignAsync(AssignRoleRequest request, CancellationToken cancellationToken = default)
    {
        var exists = await _db.UserRoles.AnyAsync(ur => ur.UserId == request.UserId && ur.RoleId == request.RoleId, cancellationToken);
        if (exists)
        {
            return;
        }

        _db.UserRoles.Add(new UserRole { UserId = request.UserId, RoleId = request.RoleId });
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(
            AuditActions.PermissionChange,
            nameof(UserRole),
            $"{request.UserId}:{request.RoleId}",
            newValue: new { request.UserId, request.RoleId },
            cancellationToken: cancellationToken);
    }
}
