using BPM.Application.Common;
using BPM.Application.Roles;
using BPM.Application.Users;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BPM.Infrastructure.Services;

public class RoleService : IRoleService
{
    private const string AdministratorRole = "Administrator";

    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;
    private readonly IValidator<UpdateRoleRequest> _updateValidator;

    public RoleService(BpmDbContext db, IAuditService auditService, IValidator<UpdateRoleRequest> updateValidator)
    {
        _db = db;
        _auditService = auditService;
        _updateValidator = updateValidator;
    }

    public async Task<IReadOnlyList<RoleDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.Roles
            .AsNoTracking()
            .Select(r => new RoleDto(r.Id, r.Name, r.UserRoles.Count, Convert.ToBase64String(r.RowVersion)))
            .ToListAsync(cancellationToken);

    public async Task<RoleDto> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken = default)
    {
        // Phase 6.4 fix: this pre-check was missing entirely — unlike UserService/DepartmentService
        // (Phase 5.5.2), which both got an application-level AnyAsync duplicate check, RoleService
        // relied solely on the DB's unique index (IX_Roles_TenantId_Name), so a duplicate role name
        // threw an unhandled DbUpdateException -> an ugly 500 instead of a clean 409. Investigated
        // during Phase 6.4 (see PROGRESS.md's Phase 6.4 section) after Phase 6.3 observed this
        // surfacing as an intermittent live-test flake; confirmed to be a genuine, directly
        // reachable application bug (reproducible with two sequential single-threaded requests, not
        // just a parallel-test race), not merely a test-isolation artifact.
        var nameTaken = await _db.Roles.AnyAsync(r => r.Name == request.Name, cancellationToken);
        if (nameTaken)
        {
            throw new ConflictAppException("ROLE_NAME_TAKEN", $"A role named '{request.Name}' already exists.");
        }

        var role = new Role { Name = request.Name };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.CreateRole, nameof(Role), role.Id.ToString(), newValue: new { role.Name }, cancellationToken: cancellationToken);

        return new RoleDto(role.Id, role.Name, 0, Convert.ToBase64String(role.RowVersion));
    }

    // Phase 9 Part 22-24 — rename only. The Administrator role is protected here (Part 24):
    // renaming it away from the literal string every [Authorize(Roles="Administrator")] attribute
    // and every AdministratorRole constant in this codebase compares against would silently and
    // catastrophically strip every administrator of their access without deleting or touching a
    // single UserRole row — a far more dangerous failure mode than the self-lockout UnassignAsync
    // already guards against. No new Role.IsSystemRole column was introduced (Part 24's own
    // preference to reuse existing architecture) — the guard is a plain name comparison against
    // the same AdministratorRole constant this class already uses everywhere else.
    public async Task<RoleDto?> UpdateAsync(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var role = await _db.Roles.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role is null)
        {
            return null;
        }

        if (role.Name == AdministratorRole && request.Name != AdministratorRole)
        {
            throw new ConflictAppException("CANNOT_RENAME_ADMINISTRATOR_ROLE", "The Administrator role cannot be renamed.");
        }

        if (request.Name != role.Name)
        {
            var nameTaken = await _db.Roles.AnyAsync(r => r.Id != id && r.Name == request.Name, cancellationToken);
            if (nameTaken)
            {
                throw new ConflictAppException("ROLE_NAME_TAKEN", $"A role named '{request.Name}' already exists.");
            }
        }

        var oldName = role.Name;
        role.Name = request.Name;

        byte[] expectedVersion;
        try
        {
            expectedVersion = Convert.FromBase64String(request.ExpectedVersion);
        }
        catch (FormatException)
        {
            throw new BadRequestAppException("INVALID_EXPECTED_VERSION", "ExpectedVersion must be a base64-encoded RowVersion.");
        }
        _db.Entry(role).Property(r => r.RowVersion).OriginalValue = expectedVersion;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("ROLE_CONCURRENCY_CONFLICT", "This role was modified by another administrator. Reload and retry.");
        }

        await _auditService.LogAsync(AuditActions.ModifyRole, nameof(Role), role.Id.ToString(), oldValue: new { Name = oldName }, newValue: new { role.Name }, cancellationToken: cancellationToken);

        var memberCount = await _db.UserRoles.CountAsync(ur => ur.RoleId == id, cancellationToken);
        return new RoleDto(role.Id, role.Name, memberCount, Convert.ToBase64String(role.RowVersion));
    }

    public async Task<IReadOnlyList<UserDto>> GetMembersAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.RoleId == roleId)
            .Select(ur => ur.User!)
            .Select(u => new UserDto(u.Id, u.Username, u.DisplayName, u.Email, u.DepartmentId, u.IsActive, u.LastLoginAt, Convert.ToBase64String(u.RowVersion)))
            .ToListAsync(cancellationToken);

    public async Task AssignAsync(AssignRoleRequest request, CancellationToken cancellationToken = default)
    {
        var exists = await _db.UserRoles.AnyAsync(ur => ur.UserId == request.UserId && ur.RoleId == request.RoleId, cancellationToken);
        if (exists)
        {
            return;
        }

        _db.UserRoles.Add(new UserRole { UserId = request.UserId, RoleId = request.RoleId });

        // Phase 10 — the AnyAsync check above is a fast-path optimization only; it cannot close
        // the race between two genuinely concurrent Assign requests for the same (UserId, RoleId)
        // pair, both of which can pass the check before either commits. UserRole's own composite
        // primary key (UserId, RoleId) is the real guard — the loser's SaveChangesAsync throws a
        // Postgres unique-violation, which used to surface as an unhandled 500. Since the intended
        // end state (this UserRole existing) was already achieved by whichever request won the
        // race, the loser treats it the same as the already-assigned fast path above: a harmless,
        // idempotent no-op, not an error — matching this method's own stated idempotent contract.
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return;
        }

        await _auditService.LogAsync(
            AuditActions.PermissionChange,
            nameof(UserRole),
            $"{request.UserId}:{request.RoleId}",
            newValue: new { request.UserId, request.RoleId },
            cancellationToken: cancellationToken);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public async Task UnassignAsync(UnassignRoleRequest request, Guid actingUserId, CancellationToken cancellationToken = default)
    {
        var userRole = await _db.UserRoles.SingleOrDefaultAsync(ur => ur.UserId == request.UserId && ur.RoleId == request.RoleId, cancellationToken);
        if (userRole is null)
        {
            return;
        }

        // Phase 5.5.2 self-lockout protection (§26): RolesController is already fully
        // Administrator-only at the controller level, so a non-admin can never reach this method
        // at all — privilege *escalation* is already structurally impossible. The remaining risk
        // this guards against is an Administrator accidentally removing their *own* Administrator
        // membership and locking themselves (and potentially the whole tenant, if they were the
        // only admin) out with no other admin able to undo it via the UI. Removing someone
        // *else's* Administrator role is still allowed — that is a deliberate admin action another
        // admin can perform and audit, not a lockout risk.
        if (request.UserId == actingUserId)
        {
            var role = await _db.Roles.AsNoTracking().SingleAsync(r => r.Id == request.RoleId, cancellationToken);
            if (role.Name == AdministratorRole)
            {
                throw new ConflictAppException("CANNOT_REMOVE_OWN_ADMINISTRATOR_ROLE", "You cannot remove your own Administrator role.");
            }
        }

        _db.UserRoles.Remove(userRole);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(
            AuditActions.PermissionChange,
            nameof(UserRole),
            $"{request.UserId}:{request.RoleId}",
            oldValue: new { request.UserId, request.RoleId },
            cancellationToken: cancellationToken);
    }
}
