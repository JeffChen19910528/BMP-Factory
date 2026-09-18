using BPM.Application.Users;

namespace BPM.Application.Roles;

// Phase 5.5.2: MemberCount added — the Roles workspace needs "N users" per role (per the phase's
// own mockup) and there was previously no way to know this without a new query; a plain aggregate
// alongside the existing Id/Name, not a second membership model.
// Phase 9 — RowVersion added, same base64 RowVersion/ExpectedVersion convention as every other
// AuditableEntity DTO — Role already had the column (Phase 1) but nothing surfaced it until now,
// since there was no rename/Update endpoint to need it.
public record RoleDto(Guid Id, string Name, int MemberCount, string RowVersion);

public record CreateRoleRequest(string Name);

// Phase 9 Part 22/23 — rename only; Role has no other mutable metadata to justify inventing more
// fields for. ExpectedVersion follows the same convention as every other Update request.
public record UpdateRoleRequest(string Name, string ExpectedVersion);

public record AssignRoleRequest(Guid UserId, Guid RoleId);

// Phase 5.5.2: symmetric counterpart to AssignRoleRequest — Phase 1 only ever added a UserRole
// row (Assign), there was no way to remove one at all.
public record UnassignRoleRequest(Guid UserId, Guid RoleId);

public interface IRoleService
{
    Task<IReadOnlyList<RoleDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<RoleDto> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken = default);

    // Phase 9 Part 22-24 — rename only. Returns null if the role doesn't exist. Renaming the
    // literal "Administrator" role is rejected (see RoleService's own guard) — a normal rename
    // must never be able to destroy the one role every Administrator-only authorization check in
    // this codebase compares against by name.
    Task<RoleDto?> UpdateAsync(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken = default);

    Task AssignAsync(AssignRoleRequest request, CancellationToken cancellationToken = default);

    // Phase 5.5.2: `actingUserId` is the authenticated caller (never client-supplied) — required
    // so the self-lockout guard below can compare "whose Administrator membership is this" against
    // "who is asking to remove it," entirely server-side.
    Task UnassignAsync(UnassignRoleRequest request, Guid actingUserId, CancellationToken cancellationToken = default);

    // Phase 5.5.2: there was previously no way to see who holds a role at all — RoleDto.MemberCount
    // answers "how many," this answers "who."
    Task<IReadOnlyList<UserDto>> GetMembersAsync(Guid roleId, CancellationToken cancellationToken = default);
}
