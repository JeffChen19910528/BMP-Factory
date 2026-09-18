using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Sla;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BPM.Tests.Workflow;

// Phase 6.3 — SlaPolicyService CRUD, against a real Postgres database, mirroring
// DepartmentServiceTests.cs's own structure (the closest existing precedent: an
// AuditableEntity-backed, admin-editable resource with the RowVersion/ExpectedVersion pattern).
[Collection("Postgres")]
public class SlaPolicyServiceTests
{
    private static SlaPolicyService NewService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new CreateSlaPolicyRequestValidator(), new UpdateSlaPolicyRequestValidator());

    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static async Task<Guid> CreateProcessDefinitionAsync(BpmDbContext db)
    {
        var definition = await NewDefinitionService(db).CreateAsync(new CreateProcessDefinitionRequest($"sla-test-{Guid.NewGuid():N}", "SLA Test Process", null, null));
        return definition.Id;
    }

    [Fact]
    public async Task CreateAsync_Valid_Succeeds()
    {
        await using var db = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(db);

        var policy = await NewService(db).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 1440, 720));

        Assert.Equal("approval", policy.NodeId);
        Assert.True(policy.Enabled);
        Assert.NotEmpty(policy.RowVersion);
    }

    [Fact]
    public async Task CreateAsync_ZeroDuration_Rejected()
    {
        await using var db = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(db);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            NewService(db).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 0, 0)));
    }

    [Fact]
    public async Task CreateAsync_WarningOffsetEqualToDuration_Rejected()
    {
        await using var db = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(db);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            NewService(db).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 60, 60)));
    }

    [Fact]
    public async Task CreateAsync_InvalidProcessDefinition_Rejected()
    {
        await using var db = PostgresFixture.CreateContext();

        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).CreateAsync(new CreateSlaPolicyRequest(Guid.NewGuid(), "approval", true, 60, 30)));
        Assert.Equal("INVALID_PROCESS_DEFINITION", ex.Code);
    }

    [Fact]
    public async Task CreateAsync_DuplicateForSameProcessAndNode_Rejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await NewService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 60, 30));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 120, 60)));
        Assert.Equal("SLA_POLICY_ALREADY_EXISTS", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_Valid_Succeeds_AndAdvancesRowVersion()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 60, 30));

        await using var updateDb = PostgresFixture.CreateContext();
        var updated = await NewService(updateDb).UpdateAsync(created.Id, new UpdateSlaPolicyRequest(true, 120, 60, created.RowVersion));

        Assert.NotNull(updated);
        Assert.Equal(120, updated!.DurationMinutes);
        Assert.NotEqual(created.RowVersion, updated.RowVersion);
    }

    [Fact]
    public async Task UpdateAsync_StaleExpectedVersion_RejectedWithConflict()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 60, 30));

        await using var writerADb = PostgresFixture.CreateContext();
        await NewService(writerADb).UpdateAsync(created.Id, new UpdateSlaPolicyRequest(true, 90, 30, created.RowVersion));

        await using var writerBDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(writerBDb).UpdateAsync(created.Id, new UpdateSlaPolicyRequest(true, 120, 30, created.RowVersion)));
        Assert.Equal("SLA_POLICY_CONCURRENCY_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_NonexistentPolicy_ReturnsNull()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).UpdateAsync(Guid.NewGuid(), new UpdateSlaPolicyRequest(true, 60, 30, Convert.ToBase64String(new byte[8])));
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_DisablingPolicy_DoesNotAffectExistingRecords()
    {
        // This service has no knowledge of TaskSla at all (by design — see SlaPolicyService's own
        // comment) — this test documents that boundary rather than exercising SlaEngine directly
        // (covered in SlaIntegrationTests.cs).
        await using var createDb = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 60, 30));

        await using var db = PostgresFixture.CreateContext();
        var disabled = await NewService(db).UpdateAsync(created.Id, new UpdateSlaPolicyRequest(false, 60, 30, created.RowVersion));

        Assert.NotNull(disabled);
        Assert.False(disabled!.Enabled);
    }

    // ---- Phase 9 Part 5: audit coverage (this service had none before) ----

    [Fact]
    public async Task CreateAsync_WritesAuditEntry()
    {
        await using var db = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(db);

        var policy = await NewService(db).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 60, 30));

        var log = await db.AuditLogs.SingleAsync(a => a.Action == "CreateSlaPolicy" && a.EntityId == policy.Id.ToString());
        Assert.Equal(nameof(BPM.Domain.Entities.SlaPolicy), log.EntityType);
    }

    [Fact]
    public async Task UpdateAsync_WritesAuditEntry()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 60, 30));

        await using var db = PostgresFixture.CreateContext();
        await NewService(db).UpdateAsync(created.Id, new UpdateSlaPolicyRequest(true, 120, 60, created.RowVersion));

        var log = await db.AuditLogs.SingleAsync(a => a.Action == "UpdateSlaPolicy" && a.EntityId == created.Id.ToString());
        Assert.Equal(nameof(BPM.Domain.Entities.SlaPolicy), log.EntityType);
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
