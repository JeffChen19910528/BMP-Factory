using BPM.Application.Common;
using BPM.Application.Departments;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using BPM.Tests.Workflow;
using Xunit;

namespace BPM.Tests.Administration;

// Phase 5.5.2 — Administration Foundation. Department previously had Create + GetAll only; this
// phase added Update (needed for the Departments workspace's own "edit" requirement) plus the
// validation an Update genuinely made possible for the first time — self-parent and circular-
// parent cycles can only be introduced by re-parenting an *existing* department, which Create
// alone could never do.
[Collection("Postgres")]
public class DepartmentServiceTests
{
    private static DepartmentService NewService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new CreateDepartmentRequestValidator(), new UpdateDepartmentRequestValidator());

    private static async Task<Guid> CreateOrganizationAsync(BpmDbContext db)
    {
        var org = new Organization { Name = $"Org-{Guid.NewGuid():N}" };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    [Fact]
    public async Task CreateAsync_Valid_Succeeds()
    {
        await using var db = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(db);

        var department = await NewService(db).CreateAsync(new CreateDepartmentRequest("Engineering", orgId, null, null));

        Assert.Equal("Engineering", department.Name);
        Assert.NotEmpty(department.RowVersion);
    }

    [Fact]
    public async Task CreateAsync_DuplicateNameInSameOrganization_Rejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(setupDb);
        await NewService(setupDb).CreateAsync(new CreateDepartmentRequest("Finance", orgId, null, null));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() => NewService(db).CreateAsync(new CreateDepartmentRequest("Finance", orgId, null, null)));
        Assert.Equal("DEPARTMENT_NAME_TAKEN", ex.Code);
    }

    [Fact]
    public async Task CreateAsync_SameNameInDifferentOrganizations_Allowed()
    {
        await using var db = PostgresFixture.CreateContext();
        var orgA = await CreateOrganizationAsync(db);
        var orgB = await CreateOrganizationAsync(db);
        var service = NewService(db);

        await service.CreateAsync(new CreateDepartmentRequest("Finance", orgA, null, null));
        var second = await service.CreateAsync(new CreateDepartmentRequest("Finance", orgB, null, null));

        Assert.Equal("Finance", second.Name);
    }

    [Fact]
    public async Task UpdateAsync_Rename_Succeeds_AndAdvancesRowVersion()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateDepartmentRequest("Old Name", orgId, null, null));

        await using var updateDb = PostgresFixture.CreateContext();
        var updated = await NewService(updateDb).UpdateAsync(created.Id, new UpdateDepartmentRequest("New Name", null, null, created.RowVersion));

        Assert.NotNull(updated);
        Assert.Equal("New Name", updated!.Name);
        Assert.NotEqual(created.RowVersion, updated.RowVersion);
    }

    [Fact]
    public async Task UpdateAsync_SelfParent_Rejected()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateDepartmentRequest("Dept", orgId, null, null));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).UpdateAsync(created.Id, new UpdateDepartmentRequest("Dept", created.Id, null, created.RowVersion)));
        Assert.Equal("INVALID_PARENT_DEPARTMENT", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_CircularParent_Rejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(setupDb);
        var service = NewService(setupDb);
        var a = await service.CreateAsync(new CreateDepartmentRequest("A", orgId, null, null));
        var b = await service.CreateAsync(new CreateDepartmentRequest("B", orgId, a.Id, null));
        var c = await service.CreateAsync(new CreateDepartmentRequest("C", orgId, b.Id, null));

        // A -> B -> C already exists; making A's parent = C would close the loop.
        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).UpdateAsync(a.Id, new UpdateDepartmentRequest("A", c.Id, null, a.RowVersion)));
        Assert.Equal("CIRCULAR_DEPARTMENT_HIERARCHY", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_ValidReparenting_Succeeds()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(setupDb);
        var service = NewService(setupDb);
        var a = await service.CreateAsync(new CreateDepartmentRequest("A", orgId, null, null));
        var b = await service.CreateAsync(new CreateDepartmentRequest("B", orgId, null, null));

        await using var db = PostgresFixture.CreateContext();
        var updated = await NewService(db).UpdateAsync(b.Id, new UpdateDepartmentRequest("B", a.Id, null, b.RowVersion));

        Assert.Equal(a.Id, updated!.ParentId);
    }

    [Fact]
    public async Task UpdateAsync_InvalidParent_NotFound_Rejected()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateDepartmentRequest("Dept", orgId, null, null));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).UpdateAsync(created.Id, new UpdateDepartmentRequest("Dept", Guid.NewGuid(), null, created.RowVersion)));
        Assert.Equal("INVALID_PARENT_DEPARTMENT", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_StaleExpectedVersion_RejectedWithConflict()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateDepartmentRequest("Dept", orgId, null, null));

        await using var writerADb = PostgresFixture.CreateContext();
        await NewService(writerADb).UpdateAsync(created.Id, new UpdateDepartmentRequest("Writer A", null, null, created.RowVersion));

        await using var writerBDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(writerBDb).UpdateAsync(created.Id, new UpdateDepartmentRequest("Writer B", null, null, created.RowVersion)));
        Assert.Equal("DEPARTMENT_CONCURRENCY_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task CreateAsync_NonexistentManager_Rejected()
    {
        await using var db = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(db);

        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).CreateAsync(new CreateDepartmentRequest("Dept", orgId, null, Guid.NewGuid())));
        Assert.Equal("INVALID_MANAGER", ex.Code);
    }

    [Fact]
    public async Task CreateAsync_InactiveManager_Rejected()
    {
        await using var db = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(db);
        var inactiveManager = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = "Inactive", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = false };
        db.Users.Add(inactiveManager);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).CreateAsync(new CreateDepartmentRequest("Dept", orgId, null, inactiveManager.Id)));
        Assert.Equal("INVALID_MANAGER", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_InvalidManager_Rejected()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var orgId = await CreateOrganizationAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateDepartmentRequest("Dept", orgId, null, null));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).UpdateAsync(created.Id, new UpdateDepartmentRequest("Dept", null, Guid.NewGuid(), created.RowVersion)));
        Assert.Equal("INVALID_MANAGER", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_NonexistentDepartment_ReturnsNull()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).UpdateAsync(Guid.NewGuid(), new UpdateDepartmentRequest("Dept", null, null, Convert.ToBase64String(new byte[8])));
        Assert.Null(result);
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
