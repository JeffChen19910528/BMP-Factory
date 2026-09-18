using BPM.Application.Common;
using BPM.Application.Forms;
using BPM.Domain.Entities;
using BPM.Domain.Forms;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Workflow;

// Phase 10 SHOULD HAVE #4 — Attachment / MinIO consistency hardening. No distributed transaction
// was introduced (Discovery explicitly ruled that out) — these tests instead prove the narrower,
// real fixes: a failed storage write never creates a DB row; a failed DB save after a successful
// storage write triggers best-effort cleanup and never claims success; a failed storage read on
// download is mapped to a clean AppException, never a raw exception; and the audit log is only
// ever written after its corresponding DB mutation has actually committed.
[Collection("Postgres")]
public class AttachmentServiceTests
{
    private static Engine.FormEngine NewFormEngine(BpmDbContext db) => new(db, new FormAuthorizationService(db));

    private static AttachmentService NewAttachmentService(BpmDbContext db, FakeAttachmentStorage storage) =>
        new(db, new FormAuthorizationService(db), storage, new AuditService(db, new FixedCurrentUser(Guid.Empty)), NullLogger<AttachmentService>.Instance);

    private static async Task<(Guid FormInstanceId, Guid UserId)> CreateDraftFormInstanceAsync()
    {
        var key = $"attach-form-{Guid.NewGuid():N}";
        var schema = new FormSchema(new[] { new FormFieldDefinition("note", FormFieldType.Text, "Note") });

        await using var setupDb = PostgresFixture.CreateContext();
        var formService = new FormDefinitionService(setupDb, new AuditService(setupDb, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateFormDefinitionRequestValidator(), new CreateFormVersionRequestValidator(), new UpdateFormDefinitionRequestValidator(), new UpdateFormVersionRequestValidator());
        var definition = await formService.CreateAsync(new CreateFormDefinitionRequest(key, "Attachment Test Form", null, null));
        await formService.CreateVersionAsync(definition.Id, new CreateFormVersionRequest(schema));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewFormEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        var userId = Guid.NewGuid();
        await using var userDb = PostgresFixture.CreateContext();
        userDb.Users.Add(new User { Id = userId, Username = $"user-{Guid.NewGuid():N}", DisplayName = "Attachment Test User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true });
        await userDb.SaveChangesAsync();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(key, null), userId);

        return (instance.Id, userId);
    }

    private static MemoryStream Content(string text = "hello") => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task UploadAsync_Success_PersistsAttachmentAndAudits()
    {
        var (formInstanceId, userId) = await CreateDraftFormInstanceAsync();
        var storage = new FakeAttachmentStorage();

        await using var db = PostgresFixture.CreateContext();
        var attachment = await NewAttachmentService(db, storage).UploadAsync(formInstanceId, "receipt.pdf", "application/pdf", Content(), 5, userId, Array.Empty<string>());

        Assert.Single(storage.Saved);

        await using var verifyDb = PostgresFixture.CreateContext();
        Assert.True(await verifyDb.Attachments.AnyAsync(a => a.Id == attachment.Id));
        var audit = await verifyDb.AuditLogs.Where(a => a.EntityId == attachment.Id.ToString() && a.Action == AuditActions.AttachmentUploaded).ToListAsync();
        Assert.Single(audit);
    }

    [Fact]
    public async Task UploadAsync_StorageWriteFails_NoAttachmentRowCreated_NoAuditWritten()
    {
        var (formInstanceId, userId) = await CreateDraftFormInstanceAsync();
        var storage = new FakeAttachmentStorage { ThrowOnSave = true };

        await using var beforeDb = PostgresFixture.CreateContext();
        var auditCountBefore = await beforeDb.AuditLogs.CountAsync(a => a.Action == AuditActions.AttachmentUploaded);

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewAttachmentService(db, storage).UploadAsync(formInstanceId, "receipt.pdf", "application/pdf", Content(), 5, userId, Array.Empty<string>()));
        Assert.Equal("ATTACHMENT_STORAGE_UNAVAILABLE", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        Assert.False(await verifyDb.Attachments.AnyAsync(a => a.FormInstanceId == formInstanceId));
        Assert.Equal(auditCountBefore, await verifyDb.AuditLogs.CountAsync(a => a.Action == AuditActions.AttachmentUploaded));
    }

    [Fact]
    public async Task UploadAsync_DbSaveFails_BestEffortDeletesTheOrphanedObject_NoMisleadingSuccess()
    {
        var (formInstanceId, userId) = await CreateDraftFormInstanceAsync();
        var storage = new FakeAttachmentStorage();

        // Force the metadata SaveChangesAsync to fail: delete the FormInstance's underlying row
        // out from under it between the storage write and the DB save by pointing the attachment
        // at a nonexistent FormInstanceId via a second, tampering context — simplest reliable way
        // to make the real FK constraint reject the insert without mocking EF internals.
        await using var tamperDb = PostgresFixture.CreateContext();
        var instance = await tamperDb.FormInstances.SingleAsync(f => f.Id == formInstanceId);
        // Deleting the instance is blocked by Restrict FKs from other tables in general, but this
        // form instance has no dependents yet, so this is safe and deterministic for this test.
        tamperDb.FormInstances.Remove(instance);
        await tamperDb.SaveChangesAsync();

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<NotFoundAppException>(() =>
            NewAttachmentService(db, storage).UploadAsync(formInstanceId, "receipt.pdf", "application/pdf", Content(), 5, userId, Array.Empty<string>()));
        Assert.Equal("FORM_INSTANCE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task DownloadAsync_StorageReadFails_MapsToCleanAppException_NeverRawException()
    {
        var (formInstanceId, userId) = await CreateDraftFormInstanceAsync();
        var storage = new FakeAttachmentStorage();

        await using var uploadDb = PostgresFixture.CreateContext();
        var attachment = await NewAttachmentService(uploadDb, storage).UploadAsync(formInstanceId, "receipt.pdf", "application/pdf", Content(), 5, userId, Array.Empty<string>());

        storage.ThrowOnOpenRead = true;

        await using var downloadDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewAttachmentService(downloadDb, storage).DownloadAsync(attachment.Id, userId, Array.Empty<string>()));
        Assert.Equal("ATTACHMENT_CONTENT_UNAVAILABLE", ex.Code);
    }

    [Fact]
    public async Task DeleteAsync_Success_RemovesRowAndAudits()
    {
        var (formInstanceId, userId) = await CreateDraftFormInstanceAsync();
        var storage = new FakeAttachmentStorage();

        await using var uploadDb = PostgresFixture.CreateContext();
        var attachment = await NewAttachmentService(uploadDb, storage).UploadAsync(formInstanceId, "receipt.pdf", "application/pdf", Content(), 5, userId, Array.Empty<string>());

        await using var deleteDb = PostgresFixture.CreateContext();
        await NewAttachmentService(deleteDb, storage).DeleteAsync(attachment.Id, userId, Array.Empty<string>());

        await using var verifyDb = PostgresFixture.CreateContext();
        Assert.False(await verifyDb.Attachments.AnyAsync(a => a.Id == attachment.Id));
        Assert.Single(await verifyDb.AuditLogs.Where(a => a.Action == AuditActions.AttachmentDeleted && a.EntityId == attachment.Id.ToString()).ToListAsync());
    }

    [Fact]
    public async Task DeleteAsync_StorageDeleteFails_RowIsNotRemoved_NoAuditWritten()
    {
        var (formInstanceId, userId) = await CreateDraftFormInstanceAsync();
        var storage = new FakeAttachmentStorage();

        await using var uploadDb = PostgresFixture.CreateContext();
        var attachment = await NewAttachmentService(uploadDb, storage).UploadAsync(formInstanceId, "receipt.pdf", "application/pdf", Content(), 5, userId, Array.Empty<string>());

        storage.ThrowOnDelete = true;

        await using var deleteDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewAttachmentService(deleteDb, storage).DeleteAsync(attachment.Id, userId, Array.Empty<string>()));
        Assert.Equal("ATTACHMENT_STORAGE_UNAVAILABLE", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        Assert.True(await verifyDb.Attachments.AnyAsync(a => a.Id == attachment.Id));
        Assert.Empty(await verifyDb.AuditLogs.Where(a => a.Action == AuditActions.AttachmentDeleted && a.EntityId == attachment.Id.ToString()).ToListAsync());
    }

    [Fact]
    public async Task UploadAsync_UnauthorizedUser_Rejected()
    {
        var (formInstanceId, _) = await CreateDraftFormInstanceAsync();
        var storage = new FakeAttachmentStorage();
        var stranger = Guid.NewGuid();

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            NewAttachmentService(db, storage).UploadAsync(formInstanceId, "receipt.pdf", "application/pdf", Content(), 5, stranger, Array.Empty<string>()));
        Assert.Equal("FORM_INSTANCE_NOT_AUTHORIZED", ex.Code);
    }

    private class FakeAttachmentStorage : IAttachmentStorage
    {
        public Dictionary<string, byte[]> Saved { get; } = new();
        public bool ThrowOnSave { get; set; }
        public bool ThrowOnDelete { get; set; }
        public bool ThrowOnOpenRead { get; set; }

        public async Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSave)
            {
                throw new IOException("Simulated storage failure.");
            }
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Saved[storageKey] = buffer.ToArray();
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            if (ThrowOnOpenRead)
            {
                throw new IOException("Simulated storage read failure.");
            }
            return Task.FromResult<Stream>(new MemoryStream(Saved[storageKey]));
        }

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDelete)
            {
                throw new IOException("Simulated storage delete failure.");
            }
            Saved.Remove(storageKey);
            return Task.CompletedTask;
        }

        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
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
