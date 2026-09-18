using BPM.Application.Common;
using BPM.Application.Roles;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using BPM.Tests.Workflow;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BPM.Tests.Administration;

// Phase 5.5.2 — Administration Foundation. Role/UserRole already existed (Phase 1: Create +
// GetAll + Assign only); this phase added member visibility (MemberCount, GetMembersAsync),
// Unassign (previously there was no way to remove a role from a user at all), and self-lockout
// protection on removing one's own Administrator membership. Privilege *escalation* by a
// non-administrator was already structurally impossible (RolesController is
// [Authorize(Roles = "Administrator")] at the controller level, unchanged) — not re-tested here
// at the service layer since the service has no authorization logic of its own to test; the live
// acceptance scenario proves the controller-level gate over real HTTP instead.
[Collection("Postgres")]
public class RoleServiceTests
{
    private const string AdministratorRole = "Administrator";

    private static RoleService NewService(BpmDbContext db) => new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new UpdateRoleRequestValidator());

    private static async Task<Guid> CreateUserAsync(BpmDbContext db)
    {
        var user = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = "Test User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    // The "Administrator" role is seeded once per tenant (DbSeeder) and Role.Name is unique per
    // tenant, so tests exercising the self-lockout guard (which matches on the literal name
    // "Administrator") must reuse the seeded row rather than creating a second one.
    private static async Task<Guid> GetAdministratorRoleIdAsync(BpmDbContext db) =>
        (await db.Roles.AsNoTracking().SingleAsync(r => r.Name == AdministratorRole)).Id;

    [Fact]
    public async Task GetAllAsync_ReportsMemberCount()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var role = await service.CreateAsync(new CreateRoleRequest($"Role-{Guid.NewGuid():N}"));
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        await service.AssignAsync(new AssignRoleRequest(userA, role.Id));
        await service.AssignAsync(new AssignRoleRequest(userB, role.Id));

        await using var db = PostgresFixture.CreateContext();
        var roles = await NewService(db).GetAllAsync();

        Assert.Equal(2, roles.Single(r => r.Id == role.Id).MemberCount);
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_IsRejectedWithConflict_NotAnUnhandledDbException()
    {
        // Phase 6.4 regression test: RoleService.CreateAsync previously had no duplicate-name
        // pre-check at all (unlike UserService/DepartmentService, which both got one in 5.5.2),
        // relying solely on the DB's unique index (IX_Roles_TenantId_Name) — a duplicate name threw
        // an unhandled DbUpdateException, surfacing as a 500 rather than a clean 409. This was
        // discovered as an intermittent live-test flake during Phase 6.3 and confirmed here, at the
        // service layer with two sequential single-threaded calls, to be a genuine, deterministically
        // reproducible application bug, not merely a parallel-test-only race.
        await using var setupDb = PostgresFixture.CreateContext();
        var name = $"Role-{Guid.NewGuid():N}";
        await NewService(setupDb).CreateAsync(new CreateRoleRequest(name));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).CreateAsync(new CreateRoleRequest(name)));
        Assert.Equal("ROLE_NAME_TAKEN", ex.Code);
    }

    [Fact]
    public async Task GetMembersAsync_ReturnsExactlyAssignedUsers()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var role = await service.CreateAsync(new CreateRoleRequest($"Role-{Guid.NewGuid():N}"));
        var member = await CreateUserAsync(setupDb);
        var nonMember = await CreateUserAsync(setupDb);
        await service.AssignAsync(new AssignRoleRequest(member, role.Id));

        await using var db = PostgresFixture.CreateContext();
        var members = await NewService(db).GetMembersAsync(role.Id);

        var memberIds = members.Select(m => m.Id).ToList();
        Assert.Contains(member, memberIds);
        Assert.DoesNotContain(nonMember, memberIds);
    }

    [Fact]
    public async Task AssignAsync_IsIdempotent_DoubleAssignDoesNotDuplicate()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var role = await service.CreateAsync(new CreateRoleRequest($"Role-{Guid.NewGuid():N}"));
        var user = await CreateUserAsync(setupDb);

        await service.AssignAsync(new AssignRoleRequest(user, role.Id));
        await service.AssignAsync(new AssignRoleRequest(user, role.Id));

        await using var db = PostgresFixture.CreateContext();
        var roles = await NewService(db).GetAllAsync();
        Assert.Equal(1, roles.Single(r => r.Id == role.Id).MemberCount);
    }

    // Phase 10 — regression for a real concurrency bug found by Discovery: AssignAsync's
    // AnyAsync-then-insert pre-check cannot close the race between two genuinely concurrent
    // requests for the same (UserId, RoleId) pair — both can pass the check before either
    // commits, and the loser's SaveChangesAsync used to throw an unhandled DbUpdateException
    // (UserRole's composite PK violation) -> a raw 500. Mirrors
    // AuthServiceTests.LoginAsync_ConcurrentLoginsForSameUser_BothSucceed's own real-concurrency
    // Task.WhenAll shape rather than faking it with sequential calls.
    [Fact]
    public async Task AssignAsync_TwoConcurrentRequestsForSameUserRole_BothSucceed_NoDuplicateRow()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var setupService = NewService(setupDb);
        var role = await setupService.CreateAsync(new CreateRoleRequest($"Role-{Guid.NewGuid():N}"));
        var user = await CreateUserAsync(setupDb);

        await using var db1 = PostgresFixture.CreateContext();
        await using var db2 = PostgresFixture.CreateContext();

        var assign1 = NewService(db1).AssignAsync(new AssignRoleRequest(user, role.Id));
        var assign2 = NewService(db2).AssignAsync(new AssignRoleRequest(user, role.Id));

        await Task.WhenAll(assign1, assign2);

        await using var verifyDb = PostgresFixture.CreateContext();
        var roles = await NewService(verifyDb).GetAllAsync();
        Assert.Equal(1, roles.Single(r => r.Id == role.Id).MemberCount);
    }

    [Fact]
    public async Task UnassignAsync_RemovesMembership()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var role = await service.CreateAsync(new CreateRoleRequest($"Role-{Guid.NewGuid():N}"));
        var user = await CreateUserAsync(setupDb);
        var admin = await CreateUserAsync(setupDb);
        await service.AssignAsync(new AssignRoleRequest(user, role.Id));

        await using var unassignDb = PostgresFixture.CreateContext();
        await NewService(unassignDb).UnassignAsync(new UnassignRoleRequest(user, role.Id), admin);

        await using var db = PostgresFixture.CreateContext();
        var roles = await NewService(db).GetAllAsync();
        Assert.Equal(0, roles.Single(r => r.Id == role.Id).MemberCount);
    }

    [Fact]
    public async Task UnassignAsync_NonexistentMembership_IsANoOp()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var role = await service.CreateAsync(new CreateRoleRequest($"Role-{Guid.NewGuid():N}"));
        var user = await CreateUserAsync(setupDb);
        var admin = await CreateUserAsync(setupDb);

        // No assignment was ever made — must not throw.
        await NewService(setupDb).UnassignAsync(new UnassignRoleRequest(user, role.Id), admin);
    }

    [Fact]
    public async Task UnassignAsync_RemovingSomeoneElsesAdministratorRole_IsAllowed()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var adminRoleId = await GetAdministratorRoleIdAsync(setupDb);
        var otherAdmin = await CreateUserAsync(setupDb);
        var actingAdmin = await CreateUserAsync(setupDb);
        await service.AssignAsync(new AssignRoleRequest(otherAdmin, adminRoleId));
        var before = (await service.GetAllAsync()).Single(r => r.Id == adminRoleId).MemberCount;

        await using var db = PostgresFixture.CreateContext();
        await NewService(db).UnassignAsync(new UnassignRoleRequest(otherAdmin, adminRoleId), actingAdmin);

        await using var verifyDb = PostgresFixture.CreateContext();
        var roles = await NewService(verifyDb).GetAllAsync();
        Assert.Equal(before - 1, roles.Single(r => r.Id == adminRoleId).MemberCount);
    }

    [Fact]
    public async Task UnassignAsync_RemovingOwnAdministratorRole_IsRejected_SelfLockoutProtection()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var adminRoleId = await GetAdministratorRoleIdAsync(setupDb);
        var self = await CreateUserAsync(setupDb);
        await service.AssignAsync(new AssignRoleRequest(self, adminRoleId));
        var before = (await service.GetAllAsync()).Single(r => r.Id == adminRoleId).MemberCount;

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).UnassignAsync(new UnassignRoleRequest(self, adminRoleId), self));
        Assert.Equal("CANNOT_REMOVE_OWN_ADMINISTRATOR_ROLE", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        var roles = await NewService(verifyDb).GetAllAsync();
        Assert.Equal(before, roles.Single(r => r.Id == adminRoleId).MemberCount);
    }

    [Fact]
    public async Task UnassignAsync_RemovingOwnNonAdministratorRole_IsAllowed()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var managerRole = await service.CreateAsync(new CreateRoleRequest($"Manager-{Guid.NewGuid():N}"));
        var self = await CreateUserAsync(setupDb);
        await service.AssignAsync(new AssignRoleRequest(self, managerRole.Id));

        await using var db = PostgresFixture.CreateContext();
        await NewService(db).UnassignAsync(new UnassignRoleRequest(self, managerRole.Id), self);

        await using var verifyDb = PostgresFixture.CreateContext();
        var roles = await NewService(verifyDb).GetAllAsync();
        Assert.Equal(0, roles.Single(r => r.Id == managerRole.Id).MemberCount);
    }

    // ---- Phase 9: Role rename ----

    [Fact]
    public async Task UpdateAsync_Rename_Succeeds_AndAdvancesRowVersion()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateRoleRequest($"role-{Guid.NewGuid():N}"));

        await using var updateDb = PostgresFixture.CreateContext();
        var renamed = await NewService(updateDb).UpdateAsync(created.Id, new UpdateRoleRequest($"renamed-{Guid.NewGuid():N}", created.RowVersion));

        Assert.NotNull(renamed);
        Assert.NotEqual(created.Name, renamed!.Name);
        Assert.NotEqual(created.RowVersion, renamed.RowVersion);
    }

    [Fact]
    public async Task UpdateAsync_StaleExpectedVersion_RejectedWithConflict()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateRoleRequest($"role-{Guid.NewGuid():N}"));
        await NewService(createDb).UpdateAsync(created.Id, new UpdateRoleRequest($"first-rename-{Guid.NewGuid():N}", created.RowVersion));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).UpdateAsync(created.Id, new UpdateRoleRequest($"second-rename-{Guid.NewGuid():N}", created.RowVersion)));
        Assert.Equal("ROLE_CONCURRENCY_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_NonexistentRole_ReturnsNull()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).UpdateAsync(Guid.NewGuid(), new UpdateRoleRequest("x", Convert.ToBase64String(new byte[8])));
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_DuplicateName_Rejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var roleA = await service.CreateAsync(new CreateRoleRequest($"role-a-{Guid.NewGuid():N}"));
        var roleB = await service.CreateAsync(new CreateRoleRequest($"role-b-{Guid.NewGuid():N}"));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).UpdateAsync(roleB.Id, new UpdateRoleRequest(roleA.Name, roleB.RowVersion)));
        Assert.Equal("ROLE_NAME_TAKEN", ex.Code);
    }

    // ---- Phase 9 Part 24: Administrator role safety ----

    [Fact]
    public async Task UpdateAsync_RenamingAdministratorRole_IsRejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var adminRoleId = await GetAdministratorRoleIdAsync(setupDb);
        var adminRole = (await NewService(setupDb).GetAllAsync()).Single(r => r.Id == adminRoleId);

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).UpdateAsync(adminRoleId, new UpdateRoleRequest("NotAdministratorAnymore", adminRole.RowVersion)));
        Assert.Equal("CANNOT_RENAME_ADMINISTRATOR_ROLE", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        var stillAdmin = await verifyDb.Roles.AsNoTracking().SingleAsync(r => r.Id == adminRoleId);
        Assert.Equal(AdministratorRole, stillAdmin.Name);
    }

    [Fact]
    public async Task UpdateAsync_RenamingAdministratorRoleToItself_IsAllowed_NoOpRename()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var adminRoleId = await GetAdministratorRoleIdAsync(setupDb);
        var adminRole = (await NewService(setupDb).GetAllAsync()).Single(r => r.Id == adminRoleId);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).UpdateAsync(adminRoleId, new UpdateRoleRequest(AdministratorRole, adminRole.RowVersion));

        Assert.NotNull(result);
        Assert.Equal(AdministratorRole, result!.Name);
    }

    private class FixedCurrentUser : ICurrentUserService
    {
        public FixedCurrentUser(Guid? userId) => UserId = userId;
        public Guid? UserId { get; }
        public Guid TenantId => Guid.Empty;
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}
