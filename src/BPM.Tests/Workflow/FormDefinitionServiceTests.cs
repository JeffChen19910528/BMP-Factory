using BPM.Application.Common;
using BPM.Application.Forms;
using BPM.Domain.Entities;
using BPM.Domain.Forms;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Workflow;

// Phase 5.4.1 (Form Designer Foundation) backend additions: CreatedBy/UpdatedAt stamping,
// Update{Form,Version}Async (Save Draft), FormVersion optimistic concurrency, and a standalone
// ValidateSchema — the exact same gaps Phase 5.2/5.3.2 found and fixed for ProcessDefinitionService,
// found here by inspecting FormDefinitionService before touching the frontend rather than assumed.
[Collection("Postgres")]
public class FormDefinitionServiceTests
{
    private static FormDefinitionService NewService(BpmDbContext db, Guid? currentUserId = null) =>
        new(db, new AuditService(db, new FixedCurrentUser(currentUserId)), new FixedCurrentUser(currentUserId),
            new CreateFormDefinitionRequestValidator(), new CreateFormVersionRequestValidator(), new UpdateFormDefinitionRequestValidator(), new UpdateFormVersionRequestValidator());

    private static FormSchema MinimalSchema() => new(new[]
    {
        new FormFieldDefinition("itemName", FormFieldType.Text, "Item Name", Required: true),
    });

    [Fact]
    public async Task CreateAsync_StampsCreatedByAndCreatedAt()
    {
        var userId = Guid.NewGuid();
        await using var db = PostgresFixture.CreateContext();
        var service = NewService(db, userId);

        var before = DateTime.UtcNow;
        var definition = await service.CreateAsync(new CreateFormDefinitionRequest($"form-{Guid.NewGuid():N}", "Test", null, null));

        Assert.Equal(userId, definition.CreatedBy);
        Assert.True(definition.CreatedAt >= before.AddSeconds(-1));
        Assert.Null(definition.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesMetadata_ButNotKey()
    {
        await using var db = PostgresFixture.CreateContext();
        var editorId = Guid.NewGuid();
        var definition = await NewService(db).CreateAsync(new CreateFormDefinitionRequest($"form-{Guid.NewGuid():N}", "Old Name", "Old Desc", "Old Cat"));

        var updated = await NewService(db, editorId).UpdateAsync(definition.Id, new UpdateFormDefinitionRequest("New Name", "New Desc", "New Cat"));

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
            NewService(db).UpdateAsync(Guid.NewGuid(), new UpdateFormDefinitionRequest("Name", null, null)));
        Assert.Equal("FORM_DEFINITION_NOT_FOUND", notFound.Code);
    }

    [Fact]
    public async Task CreateVersionAsync_StampsCreatedBy()
    {
        var userId = Guid.NewGuid();
        await using var db = PostgresFixture.CreateContext();
        var service = NewService(db, userId);

        var definition = await service.CreateAsync(new CreateFormDefinitionRequest($"form-{Guid.NewGuid():N}", "Test", null, null));
        var version = await service.CreateVersionAsync(definition.Id, new CreateFormVersionRequest(MinimalSchema()));

        Assert.Equal(userId, version.CreatedBy);
        Assert.Null(version.PublishedAt);
        Assert.Null(version.PublishedBy);
    }

    [Fact]
    public async Task UpdateVersionAsync_UpdatesDraftSchemaJson_AndStampsUpdatedBy()
    {
        var creator = Guid.NewGuid();
        var editor = Guid.NewGuid();
        await using var db = PostgresFixture.CreateContext();

        var definition = await NewService(db, creator).CreateAsync(new CreateFormDefinitionRequest($"form-{Guid.NewGuid():N}", "Test", null, null));
        var version = await NewService(db, creator).CreateVersionAsync(definition.Id, new CreateFormVersionRequest(MinimalSchema()));

        var updated = new FormSchema(new[]
        {
            new FormFieldDefinition("itemName", FormFieldType.Text, "Renamed Item Name", Required: true),
            new FormFieldDefinition("quantity", FormFieldType.Number, "Quantity", Required: true, Validation: new FormFieldValidation(MinValue: 1)),
        });

        var result = await NewService(db, editor).UpdateVersionAsync(definition.Id, version.Id, new UpdateFormVersionRequest(updated, version.RowVersion));

        Assert.Equal(2, result.Schema.Fields.Count);
        Assert.Equal("Renamed Item Name", result.Schema.Fields.Single(f => f.Key == "itemName").Label);
        // CreatedBy reflects who created the version (creator), not who last edited it (editor).
        Assert.Equal(creator, result.CreatedBy);
        Assert.NotEqual(editor, result.CreatedBy);
        Assert.NotEqual(version.RowVersion, result.RowVersion);
    }

    [Fact]
    public async Task UpdateVersionAsync_StaleExpectedVersion_ThrowsConcurrencyConflict()
    {
        await using var db = PostgresFixture.CreateContext();
        var definition = await NewService(db).CreateAsync(new CreateFormDefinitionRequest($"form-{Guid.NewGuid():N}", "Test", null, null));
        var version = await NewService(db).CreateVersionAsync(definition.Id, new CreateFormVersionRequest(MinimalSchema()));

        await using var userADb = PostgresFixture.CreateContext();
        await NewService(userADb).UpdateVersionAsync(definition.Id, version.Id, new UpdateFormVersionRequest(MinimalSchema(), version.RowVersion));

        await using var userBDb = PostgresFixture.CreateContext();
        var conflict = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(userBDb).UpdateVersionAsync(definition.Id, version.Id, new UpdateFormVersionRequest(MinimalSchema(), version.RowVersion)));
        Assert.Equal("FORM_VERSION_CONCURRENCY_CONFLICT", conflict.Code);
        Assert.Equal(409, conflict.StatusCode);

        await using var verifyDb = PostgresFixture.CreateContext();
        var stored = await verifyDb.FormVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id);
        Assert.NotEqual(Convert.ToBase64String(stored.RowVersion), version.RowVersion);
    }

    [Fact]
    public async Task UpdateVersionAsync_AfterConflict_ReloadingAndRetryingSucceeds()
    {
        await using var db = PostgresFixture.CreateContext();
        var definition = await NewService(db).CreateAsync(new CreateFormDefinitionRequest($"form-{Guid.NewGuid():N}", "Test", null, null));
        var version = await NewService(db).CreateVersionAsync(definition.Id, new CreateFormVersionRequest(MinimalSchema()));

        await using var userADb = PostgresFixture.CreateContext();
        var afterA = await NewService(userADb).UpdateVersionAsync(definition.Id, version.Id, new UpdateFormVersionRequest(MinimalSchema(), version.RowVersion));

        await using var userBDb = PostgresFixture.CreateContext();
        var reloaded = (await NewService(userBDb).GetVersionsAsync(definition.Id)).Single(v => v.Id == version.Id);
        Assert.Equal(afterA.RowVersion, reloaded.RowVersion);

        await using var retryDb = PostgresFixture.CreateContext();
        var retried = await NewService(retryDb).UpdateVersionAsync(definition.Id, version.Id, new UpdateFormVersionRequest(MinimalSchema(), reloaded.RowVersion));
        Assert.NotNull(retried);
    }

    [Fact]
    public async Task UpdateVersionAsync_MalformedExpectedVersion_ThrowsBadRequest()
    {
        await using var db = PostgresFixture.CreateContext();
        var definition = await NewService(db).CreateAsync(new CreateFormDefinitionRequest($"form-{Guid.NewGuid():N}", "Test", null, null));
        var version = await NewService(db).CreateVersionAsync(definition.Id, new CreateFormVersionRequest(MinimalSchema()));

        var badRequest = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).UpdateVersionAsync(definition.Id, version.Id, new UpdateFormVersionRequest(MinimalSchema(), "not-valid-base64!!")));
        Assert.Equal("INVALID_EXPECTED_VERSION", badRequest.Code);
    }

    [Fact]
    public async Task UpdateVersionAsync_RejectsPublishedVersion()
    {
        await using var db = PostgresFixture.CreateContext();
        var definition = await NewService(db).CreateAsync(new CreateFormDefinitionRequest($"form-{Guid.NewGuid():N}", "Test", null, null));
        var version = await NewService(db).CreateVersionAsync(definition.Id, new CreateFormVersionRequest(MinimalSchema()));

        await using var publishDb = PostgresFixture.CreateContext();
        await new Engine.FormEngine(publishDb, new FormAuthorizationService(publishDb)).PublishVersionAsync(definition.Id, Guid.NewGuid());

        await using var updateDb = PostgresFixture.CreateContext();
        var conflict = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(updateDb).UpdateVersionAsync(definition.Id, version.Id, new UpdateFormVersionRequest(MinimalSchema(), version.RowVersion)));
        Assert.Equal("VERSION_NOT_DRAFT", conflict.Code);
    }

    [Fact]
    public async Task UpdateVersionAsync_UnknownVersion_ThrowsNotFound()
    {
        await using var db = PostgresFixture.CreateContext();
        var definition = await NewService(db).CreateAsync(new CreateFormDefinitionRequest($"form-{Guid.NewGuid():N}", "Test", null, null));

        var notFound = await Assert.ThrowsAsync<NotFoundAppException>(() =>
            NewService(db).UpdateVersionAsync(definition.Id, Guid.NewGuid(), new UpdateFormVersionRequest(MinimalSchema(), Convert.ToBase64String(Guid.NewGuid().ToByteArray()))));
        Assert.Equal("FORM_VERSION_NOT_FOUND", notFound.Code);
    }

    [Fact]
    public async Task ValidateSchema_ValidSchema_ReturnsIsValidTrue()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = new Engine.FormEngine(db, new FormAuthorizationService(db)).ValidateSchema(MinimalSchema());
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateSchema_DuplicateFieldKeys_ReturnsStructuredError()
    {
        var broken = new FormSchema(new[]
        {
            new FormFieldDefinition("a", FormFieldType.Text, "A"),
            new FormFieldDefinition("a", FormFieldType.Text, "A dup"),
        });

        await using var db = PostgresFixture.CreateContext();
        var result = new Engine.FormEngine(db, new FormAuthorizationService(db)).ValidateSchema(broken);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "DUPLICATE_FIELD_KEY");
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
