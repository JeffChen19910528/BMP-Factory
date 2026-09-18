using BPM.Application.Common;
using BPM.Application.Organizations;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using BPM.Tests.Workflow;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BPM.Tests.Administration;

// Phase 9 — Organization Administration. Mirrors DepartmentServiceTests.cs's own structure (the
// closest existing precedent: an AuditableEntity-backed hierarchy with self-parent/circular-
// hierarchy validation and the RowVersion/ExpectedVersion concurrency pattern).
[Collection("Postgres")]
public class OrganizationServiceTests
{
    private static OrganizationService NewService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new CreateOrganizationRequestValidator(), new UpdateOrganizationRequestValidator());

    [Fact]
    public async Task CreateAsync_Valid_Succeeds_AndReturnsRowVersion()
    {
        await using var db = PostgresFixture.CreateContext();
        var org = await NewService(db).CreateAsync(new CreateOrganizationRequest($"org-{Guid.NewGuid():N}", null));

        Assert.NotEmpty(org.RowVersion);
    }

    [Fact]
    public async Task CreateAsync_EmptyName_RejectedByValidation()
    {
        await using var db = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => NewService(db).CreateAsync(new CreateOrganizationRequest("", null)));
    }

    [Fact]
    public async Task CreateAsync_InvalidParent_Rejected()
    {
        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).CreateAsync(new CreateOrganizationRequest($"org-{Guid.NewGuid():N}", Guid.NewGuid())));
        Assert.Equal("INVALID_PARENT_ORGANIZATION", ex.Code);
    }

    [Fact]
    public async Task CreateAsync_WritesAuditEntry()
    {
        await using var db = PostgresFixture.CreateContext();
        var org = await NewService(db).CreateAsync(new CreateOrganizationRequest($"org-{Guid.NewGuid():N}", null));

        var log = await db.AuditLogs.SingleAsync(a => a.Action == "CreateOrganization" && a.EntityId == org.Id.ToString());
        Assert.Equal(nameof(BPM.Domain.Entities.Organization), log.EntityType);
    }

    [Fact]
    public async Task UpdateAsync_Rename_Succeeds_AndAdvancesRowVersion()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateOrganizationRequest($"org-{Guid.NewGuid():N}", null));

        await using var db = PostgresFixture.CreateContext();
        var updated = await NewService(db).UpdateAsync(created.Id, new UpdateOrganizationRequest($"renamed-{Guid.NewGuid():N}", null, created.RowVersion));

        Assert.NotNull(updated);
        Assert.NotEqual(created.Name, updated!.Name);
        Assert.NotEqual(created.RowVersion, updated.RowVersion);
    }

    [Fact]
    public async Task UpdateAsync_ReparentToValidParent_Succeeds()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var parent = await service.CreateAsync(new CreateOrganizationRequest($"parent-{Guid.NewGuid():N}", null));
        var child = await service.CreateAsync(new CreateOrganizationRequest($"child-{Guid.NewGuid():N}", null));

        await using var db = PostgresFixture.CreateContext();
        var updated = await NewService(db).UpdateAsync(child.Id, new UpdateOrganizationRequest(child.Name, parent.Id, child.RowVersion));

        Assert.NotNull(updated);
        Assert.Equal(parent.Id, updated!.ParentId);
    }

    [Fact]
    public async Task UpdateAsync_SelfParent_Rejected()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateOrganizationRequest($"org-{Guid.NewGuid():N}", null));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).UpdateAsync(created.Id, new UpdateOrganizationRequest(created.Name, created.Id, created.RowVersion)));
        Assert.Equal("INVALID_PARENT_ORGANIZATION", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_InvalidParent_Rejected()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateOrganizationRequest($"org-{Guid.NewGuid():N}", null));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).UpdateAsync(created.Id, new UpdateOrganizationRequest(created.Name, Guid.NewGuid(), created.RowVersion)));
        Assert.Equal("INVALID_PARENT_ORGANIZATION", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_CircularHierarchy_Rejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewService(setupDb);
        var grandparent = await service.CreateAsync(new CreateOrganizationRequest($"gp-{Guid.NewGuid():N}", null));
        var parent = await service.CreateAsync(new CreateOrganizationRequest($"p-{Guid.NewGuid():N}", grandparent.Id));

        // Attempt to make grandparent a child of parent — grandparent is already parent's own
        // ancestor, so this would create a cycle.
        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).UpdateAsync(grandparent.Id, new UpdateOrganizationRequest(grandparent.Name, parent.Id, grandparent.RowVersion)));
        Assert.Equal("CIRCULAR_ORGANIZATION_HIERARCHY", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_StaleExpectedVersion_RejectedWithConflict()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateOrganizationRequest($"org-{Guid.NewGuid():N}", null));
        await NewService(createDb).UpdateAsync(created.Id, new UpdateOrganizationRequest($"first-{Guid.NewGuid():N}", null, created.RowVersion));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).UpdateAsync(created.Id, new UpdateOrganizationRequest($"second-{Guid.NewGuid():N}", null, created.RowVersion)));
        Assert.Equal("ORGANIZATION_CONCURRENCY_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_NonexistentOrganization_ReturnsNull()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).UpdateAsync(Guid.NewGuid(), new UpdateOrganizationRequest("x", null, Convert.ToBase64String(new byte[8])));
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_WritesAuditEntry()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var created = await NewService(createDb).CreateAsync(new CreateOrganizationRequest($"org-{Guid.NewGuid():N}", null));

        await using var db = PostgresFixture.CreateContext();
        await NewService(db).UpdateAsync(created.Id, new UpdateOrganizationRequest($"renamed-{Guid.NewGuid():N}", null, created.RowVersion));

        var log = await db.AuditLogs.SingleAsync(a => a.Action == "ModifyOrganization" && a.EntityId == created.Id.ToString());
        Assert.Equal(nameof(BPM.Domain.Entities.Organization), log.EntityType);
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
