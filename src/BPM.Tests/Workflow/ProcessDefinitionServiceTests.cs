using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Workflow;

// Phase 5.2 (Process Management UI) backend additions: CreatedBy/UpdatedAt stamping, search/
// status filter/pagination on the list endpoint, and Save-Draft (UpdateVersionAsync) — see
// PROGRESS.md's Phase 5.2 section for why these were gaps the frontend surfaced rather than
// something planned up front.
[Collection("Postgres")]
public class ProcessDefinitionServiceTests
{
    private static ProcessDefinitionService NewService(BpmDbContext db, Guid? currentUserId = null) =>
        new(db, new AuditService(db, new FixedCurrentUser(currentUserId)), new FixedCurrentUser(currentUserId),
            new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static WorkflowDefinition MinimalDefinition() => new(
        Nodes: new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
            new WorkflowNodeDefinition("approval", WorkflowNodeType.UserTask, "Approval", new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager")),
            new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
        },
        Transitions: new[]
        {
            new WorkflowTransitionDefinition("t1", "start", "approval"),
            new WorkflowTransitionDefinition("t2", "approval", "end"),
        });

    [Fact]
    public async Task UpdateAsync_UpdatesMetadata_ButNotKey()
    {
        await using var db = PostgresFixture.CreateContext();
        var editorId = Guid.NewGuid();
        var definition = await NewService(db).CreateAsync(new CreateProcessDefinitionRequest($"proc-{Guid.NewGuid():N}", "Old Name", "Old Desc", "Old Cat"));

        var updated = await NewService(db, editorId).UpdateAsync(definition.Id, new UpdateProcessDefinitionRequest("New Name", "New Desc", "New Cat"));

        Assert.Equal(definition.Key, updated.Key);
        Assert.Equal("New Name", updated.Name);
        Assert.Equal("New Desc", updated.Description);
        Assert.Equal("New Cat", updated.Category);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_UnknownDefinition_ThrowsNotFound()
    {
        await using var db = PostgresFixture.CreateContext();
        var notFound = await Assert.ThrowsAsync<NotFoundAppException>(() =>
            NewService(db).UpdateAsync(Guid.NewGuid(), new UpdateProcessDefinitionRequest("Name", null, null)));
        Assert.Equal("PROCESS_DEFINITION_NOT_FOUND", notFound.Code);
    }

    [Fact]
    public async Task CreateAsync_StampsCreatedByAndCreatedAt()
    {
        var userId = Guid.NewGuid();
        await using var db = PostgresFixture.CreateContext();
        var service = NewService(db, userId);

        var before = DateTime.UtcNow;
        var definition = await service.CreateAsync(new CreateProcessDefinitionRequest($"proc-{Guid.NewGuid():N}", "Test", null, null));

        Assert.Equal(userId, definition.CreatedBy);
        Assert.True(definition.CreatedAt >= before.AddSeconds(-1));
        Assert.Null(definition.UpdatedAt);
    }

    [Fact]
    public async Task CreateVersionAsync_StampsCreatedBy()
    {
        var userId = Guid.NewGuid();
        await using var db = PostgresFixture.CreateContext();
        var service = NewService(db, userId);

        var definition = await service.CreateAsync(new CreateProcessDefinitionRequest($"proc-{Guid.NewGuid():N}", "Test", null, null));
        var version = await service.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(MinimalDefinition()));

        Assert.Equal(userId, version.CreatedBy);
        Assert.Null(version.PublishedAt);
        Assert.Null(version.PublishedBy);
    }

    [Fact]
    public async Task UpdateVersionAsync_UpdatesDraftDefinitionJson_AndStampsUpdatedBy()
    {
        var creator = Guid.NewGuid();
        var editor = Guid.NewGuid();
        await using var db = PostgresFixture.CreateContext();

        var definition = await NewService(db, creator).CreateAsync(new CreateProcessDefinitionRequest($"proc-{Guid.NewGuid():N}", "Test", null, null));
        var version = await NewService(db, creator).CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(MinimalDefinition()));

        var updated = MinimalDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.UserTask, "Renamed Approval", new WorkflowAssignment(WorkflowAssignmentType.Role, "Director")),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = await NewService(db, editor).UpdateVersionAsync(definition.Id, version.Id, new UpdateProcessVersionRequest(updated, version.RowVersion));

        Assert.Equal("Renamed Approval", result.Definition.Nodes.Single(n => n.Id == "approval").Name);
        // CreatedBy reflects who created the version (creator), not who last edited it (editor) —
        // Save Draft mutates DefinitionJson/UpdatedBy/UpdatedAt on the same row, not CreatedBy.
        Assert.Equal(creator, result.CreatedBy);
        Assert.NotEqual(editor, result.CreatedBy);
        // Every successful write stamps a fresh RowVersion (BpmDbContext.StampRowVersions) — the
        // caller must see a new token to save again, or a stale save must be rejected (see the
        // concurrency-conflict tests below).
        Assert.NotEqual(version.RowVersion, result.RowVersion);
    }

    [Fact]
    public async Task UpdateVersionAsync_StaleExpectedVersion_ThrowsConcurrencyConflict()
    {
        await using var db = PostgresFixture.CreateContext();
        var definition = await NewService(db).CreateAsync(new CreateProcessDefinitionRequest($"proc-{Guid.NewGuid():N}", "Test", null, null));
        var version = await NewService(db).CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(MinimalDefinition()));

        // User A reads the draft, then saves — this stamps a new RowVersion.
        await using var userADb = PostgresFixture.CreateContext();
        await NewService(userADb).UpdateVersionAsync(definition.Id, version.Id, new UpdateProcessVersionRequest(MinimalDefinition(), version.RowVersion));

        // User B read the draft at the same original RowVersion (before A's save) and now tries
        // to save against that now-stale token.
        await using var userBDb = PostgresFixture.CreateContext();
        var conflict = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(userBDb).UpdateVersionAsync(definition.Id, version.Id, new UpdateProcessVersionRequest(MinimalDefinition(), version.RowVersion)));
        Assert.Equal("PROCESS_VERSION_CONCURRENCY_CONFLICT", conflict.Code);
        Assert.Equal(409, conflict.StatusCode);

        // The conflict must not have silently applied User B's write — the DB still reflects
        // whatever User A actually saved (same principle as WorkflowEngineIntegrationTests'
        // "real rollback proof": a losing concurrent writer must leave nothing partially applied).
        await using var verifyDb = PostgresFixture.CreateContext();
        var stored = await verifyDb.ProcessVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id);
        Assert.NotEqual(Convert.ToBase64String(stored.RowVersion), version.RowVersion);
    }

    [Fact]
    public async Task UpdateVersionAsync_AfterConflict_ReloadingAndRetryingSucceeds()
    {
        // The "safe reload/recovery path" the Designer offers after a 409: re-fetch the version
        // (picking up its current RowVersion) and save again — this must succeed cleanly.
        await using var db = PostgresFixture.CreateContext();
        var definition = await NewService(db).CreateAsync(new CreateProcessDefinitionRequest($"proc-{Guid.NewGuid():N}", "Test", null, null));
        var version = await NewService(db).CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(MinimalDefinition()));

        await using var userADb = PostgresFixture.CreateContext();
        var afterA = await NewService(userADb).UpdateVersionAsync(definition.Id, version.Id, new UpdateProcessVersionRequest(MinimalDefinition(), version.RowVersion));

        await using var userBDb = PostgresFixture.CreateContext();
        var reloaded = (await NewService(userBDb).GetVersionsAsync(definition.Id)).Single(v => v.Id == version.Id);
        Assert.Equal(afterA.RowVersion, reloaded.RowVersion);

        await using var retryDb = PostgresFixture.CreateContext();
        var retried = await NewService(retryDb).UpdateVersionAsync(definition.Id, version.Id, new UpdateProcessVersionRequest(MinimalDefinition(), reloaded.RowVersion));
        Assert.NotNull(retried);
    }

    [Fact]
    public async Task UpdateVersionAsync_MalformedExpectedVersion_ThrowsBadRequest()
    {
        await using var db = PostgresFixture.CreateContext();
        var definition = await NewService(db).CreateAsync(new CreateProcessDefinitionRequest($"proc-{Guid.NewGuid():N}", "Test", null, null));
        var version = await NewService(db).CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(MinimalDefinition()));

        var badRequest = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).UpdateVersionAsync(definition.Id, version.Id, new UpdateProcessVersionRequest(MinimalDefinition(), "not-valid-base64!!")));
        Assert.Equal("INVALID_EXPECTED_VERSION", badRequest.Code);
    }

    [Fact]
    public async Task UpdateVersionAsync_RejectsPublishedVersion()
    {
        await using var db = PostgresFixture.CreateContext();
        var definition = await NewService(db).CreateAsync(new CreateProcessDefinitionRequest($"proc-{Guid.NewGuid():N}", "Test", null, null));
        var version = await NewService(db).CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(MinimalDefinition()));

        await using var publishDb = PostgresFixture.CreateContext();
        await new Engine.WorkflowEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        await using var updateDb = PostgresFixture.CreateContext();
        var conflict = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(updateDb).UpdateVersionAsync(definition.Id, version.Id, new UpdateProcessVersionRequest(MinimalDefinition(), version.RowVersion)));
        Assert.Equal("VERSION_NOT_DRAFT", conflict.Code);
    }

    [Fact]
    public async Task UpdateVersionAsync_UnknownVersion_ThrowsNotFound()
    {
        await using var db = PostgresFixture.CreateContext();
        var definition = await NewService(db).CreateAsync(new CreateProcessDefinitionRequest($"proc-{Guid.NewGuid():N}", "Test", null, null));

        var notFound = await Assert.ThrowsAsync<NotFoundAppException>(() =>
            NewService(db).UpdateVersionAsync(definition.Id, Guid.NewGuid(), new UpdateProcessVersionRequest(MinimalDefinition(), Convert.ToBase64String(Guid.NewGuid().ToByteArray()))));
        Assert.Equal("PROCESS_VERSION_NOT_FOUND", notFound.Code);
    }

    [Fact]
    public async Task GetAllAsync_FiltersBySearchAndStatus_AndPaginates()
    {
        await using var db = PostgresFixture.CreateContext();
        var service = NewService(db);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var alpha = await service.CreateAsync(new CreateProcessDefinitionRequest($"alpha-{suffix}", $"Alpha Process {suffix}", null, null));
        var beta = await service.CreateAsync(new CreateProcessDefinitionRequest($"beta-{suffix}", $"Beta Process {suffix}", null, null));

        var searchResult = await service.GetAllAsync(new ProcessDefinitionQuery(Search: $"alpha-{suffix}"));
        Assert.Single(searchResult.Items);
        Assert.Equal(alpha.Id, searchResult.Items[0].Id);

        var statusResult = await service.GetAllAsync(new ProcessDefinitionQuery(Search: suffix, Status: ProcessDefinitionStatus.Draft));
        Assert.Equal(2, statusResult.TotalCount);

        var page1 = await service.GetAllAsync(new ProcessDefinitionQuery(Search: suffix, Page: 1, PageSize: 1));
        var page2 = await service.GetAllAsync(new ProcessDefinitionQuery(Search: suffix, Page: 2, PageSize: 1));
        Assert.Equal(2, page1.TotalCount);
        Assert.Single(page1.Items);
        Assert.Single(page2.Items);
        Assert.NotEqual(page1.Items[0].Id, page2.Items[0].Id);

        Assert.Contains(beta.Id, new[] { page1.Items[0].Id, page2.Items[0].Id });
    }

    [Fact]
    public async Task ValidateDefinition_ValidGraph_ReturnsIsValidTrue()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = new Engine.WorkflowEngine(db).ValidateDefinition(MinimalDefinition());
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateDefinition_MissingStartNode_ReturnsStructuredError()
    {
        var broken = new WorkflowDefinition(
            Nodes: new[] { new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End") },
            Transitions: Array.Empty<WorkflowTransitionDefinition>());

        await using var db = PostgresFixture.CreateContext();
        var result = new Engine.WorkflowEngine(db).ValidateDefinition(broken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "MISSING_START_NODE");
    }

    // Phase 12 — same overflow guard Phase 7.2.3 established in ProcessMonitoringQueryService,
    // now applied here too (see ProcessDefinitionService.cs's own comment).
    [Fact]
    public async Task GetAllAsync_ExtremelyLargePage_DoesNotThrow_ReturnsEmptyResult()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        await NewService(setupDb).CreateAsync(new CreateProcessDefinitionRequest($"proc-{Guid.NewGuid():N}", "Test", null, null));

        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).GetAllAsync(new ProcessDefinitionQuery(Page: int.MaxValue, PageSize: 200));

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
