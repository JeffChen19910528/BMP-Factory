using BPM.Application.Audit;
using BPM.Application.Common;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using BPM.Tests.Workflow;
using Xunit;

namespace BPM.Tests.Administration;

// Phase 5.5.2 — Administration Foundation. AuditLogQueryService already existed (accepted Page/
// PageSize and applied them) but never returned TotalCount, so no caller could build a real
// paginated table. This phase widened the return type to PagedResult<AuditLogDto> (see
// AuditLogDtos.cs's own doc comment) — these tests cover the new pagination shape and the
// pre-existing filters, using a unique EntityType per test (via IAuditService.LogAsync, the same
// write path the rest of the app uses) so tests never see each other's rows even though AuditLog
// is append-only shared state.
[Collection("Postgres")]
public class AuditLogQueryServiceTests
{
    private static IAuditService NewAuditService(BpmDbContext db, Guid actingUserId) =>
        new AuditService(db, new FixedCurrentUser(actingUserId));

    private static AuditLogQueryService NewQueryService(BpmDbContext db) => new(db);

    [Fact]
    public async Task QueryAsync_ReturnsCorrectTotalCount_AcrossPages()
    {
        var entityType = $"TestEntity-{Guid.NewGuid():N}";
        var actor = Guid.NewGuid();

        await using var writeDb = PostgresFixture.CreateContext();
        var audit = NewAuditService(writeDb, actor);
        for (var i = 0; i < 5; i++)
        {
            await audit.LogAsync("TestAction", entityType, $"entity-{i}");
        }

        await using var db = PostgresFixture.CreateContext();
        var page1 = await NewQueryService(db).QueryAsync(new AuditLogQuery(null, entityType, null, null, null, Page: 1, PageSize: 2));

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(1, page1.Page);
        Assert.Equal(2, page1.PageSize);

        var page3 = await NewQueryService(db).QueryAsync(new AuditLogQuery(null, entityType, null, null, null, Page: 3, PageSize: 2));
        Assert.Single(page3.Items);
    }

    [Fact]
    public async Task QueryAsync_FiltersByUserId()
    {
        var entityType = $"TestEntity-{Guid.NewGuid():N}";
        var actorA = Guid.NewGuid();
        var actorB = Guid.NewGuid();

        await using var writeDb = PostgresFixture.CreateContext();
        await NewAuditService(writeDb, actorA).LogAsync("TestAction", entityType, "e1");
        await NewAuditService(writeDb, actorB).LogAsync("TestAction", entityType, "e2");

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).QueryAsync(new AuditLogQuery(actorA, entityType, null, null, null));

        Assert.Equal(1, result.TotalCount);
        Assert.All(result.Items, item => Assert.Equal(actorA, item.UserId));
    }

    [Fact]
    public async Task QueryAsync_FiltersByEntityId()
    {
        var entityType = $"TestEntity-{Guid.NewGuid():N}";
        var actor = Guid.NewGuid();

        await using var writeDb = PostgresFixture.CreateContext();
        var audit = NewAuditService(writeDb, actor);
        await audit.LogAsync("TestAction", entityType, "target-1");
        await audit.LogAsync("TestAction", entityType, "target-2");

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).QueryAsync(new AuditLogQuery(null, entityType, "target-1", null, null));

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("target-1", result.Items.Single().EntityId);
    }

    [Fact]
    public async Task QueryAsync_FiltersByDateRange()
    {
        var entityType = $"TestEntity-{Guid.NewGuid():N}";
        var actor = Guid.NewGuid();

        await using var writeDb = PostgresFixture.CreateContext();
        await NewAuditService(writeDb, actor).LogAsync("TestAction", entityType, "e1");

        await using var db = PostgresFixture.CreateContext();
        var future = DateTime.UtcNow.AddDays(1);
        var farFuture = DateTime.UtcNow.AddDays(2);
        var result = await NewQueryService(db).QueryAsync(new AuditLogQuery(null, entityType, null, future, farFuture));

        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task QueryAsync_NoMatches_ReturnsEmptyPagedResult()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).QueryAsync(new AuditLogQuery(null, $"NoSuchEntity-{Guid.NewGuid():N}", null, null, null));

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task QueryAsync_InvalidPageOrPageSize_FallsBackToDefaults()
    {
        var entityType = $"TestEntity-{Guid.NewGuid():N}";
        var actor = Guid.NewGuid();

        await using var writeDb = PostgresFixture.CreateContext();
        await NewAuditService(writeDb, actor).LogAsync("TestAction", entityType, "e1");

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).QueryAsync(new AuditLogQuery(null, entityType, null, null, null, Page: 0, PageSize: 0));

        Assert.Equal(1, result.Page);
        Assert.Equal(50, result.PageSize);
        Assert.Equal(1, result.TotalCount);
    }

    // Phase 12 — same overflow guard Phase 7.2.3 established in ProcessMonitoringQueryService,
    // now applied here too (see AuditLogQueryService.cs's own comment).
    [Fact]
    public async Task QueryAsync_ExtremelyLargePage_DoesNotThrow_ReturnsEmptyResult()
    {
        var entityType = $"TestEntity-{Guid.NewGuid():N}";
        await using var writeDb = PostgresFixture.CreateContext();
        await NewAuditService(writeDb, Guid.NewGuid()).LogAsync("TestAction", entityType, "e1");

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).QueryAsync(new AuditLogQuery(null, entityType, null, null, null, Page: int.MaxValue, PageSize: 200));

        Assert.Empty(result.Items);
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
