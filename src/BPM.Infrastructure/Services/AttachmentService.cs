using System.Security.Cryptography;
using BPM.Application.Common;
using BPM.Application.Forms;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BPM.Infrastructure.Services;

// Attachment upload/download/list/delete (Skill.md Phase 4 §28/§29). Metadata lives in Postgres
// (the Attachment entity); bytes go through IAttachmentStorage (MinIO). Authorization is the same
// IFormAuthorizationService FormEngine/FormInstanceQueryService use, so "who can touch this form"
// is answered in exactly one place regardless of whether it's data, or a file attached to it.
//
// Phase 10 — PostgreSQL and MinIO are never one transaction (no distributed transaction was
// introduced; Discovery explicitly ruled that out). What changed is narrower: (1) the audit call
// for a successful Upload/Delete only ever fires after the corresponding _db.SaveChangesAsync has
// actually committed, never before — so AuditLog can no longer claim a mutation succeeded when it
// didn't; (2) Upload best-effort deletes the just-written MinIO object if the DB save that should
// follow it fails, logging (never swallowing) a cleanup failure rather than presenting it as
// success; (3) Download no longer lets a raw storage exception (e.g. a dangling Attachment row
// whose object was somehow already removed) escape past this service — it's mapped to the same
// structured {code, message, traceId} contract every other failure in this codebase already uses.
public class AttachmentService : IAttachmentService
{
    // Deliberately conservative allowlist (Skill.md §29: validate file size/name/content type/
    // extension, never trust the client's declared MIME type) — cross-checked so a client can't
    // claim "image/png" for a ".exe". Extend only with real business need.
    private static readonly Dictionary<string, string[]> AllowedExtensionsByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = new[] { ".pdf" },
        ["image/png"] = new[] { ".png" },
        ["image/jpeg"] = new[] { ".jpg", ".jpeg" },
        ["text/csv"] = new[] { ".csv" },
        ["text/plain"] = new[] { ".txt" },
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = new[] { ".docx" },
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = new[] { ".xlsx" },
    };

    private const long MaxFileSizeBytes = 25 * 1024 * 1024;

    private readonly BpmDbContext _db;
    private readonly IFormAuthorizationService _authorization;
    private readonly IAttachmentStorage _storage;
    private readonly IAuditService _auditService;
    private readonly ILogger<AttachmentService> _logger;

    public AttachmentService(BpmDbContext db, IFormAuthorizationService authorization, IAttachmentStorage storage, IAuditService auditService, ILogger<AttachmentService> logger)
    {
        _db = db;
        _authorization = authorization;
        _storage = storage;
        _auditService = auditService;
        _logger = logger;
    }

    public async Task<AttachmentDto> UploadAsync(Guid formInstanceId, string fileName, string contentType, Stream content, long size, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var instance = await _db.FormInstances.SingleOrDefaultAsync(f => f.Id == formInstanceId, cancellationToken)
            ?? throw new NotFoundAppException("FORM_INSTANCE_NOT_FOUND", $"Form instance '{formInstanceId}' was not found.");

        if (!await _authorization.CanEditAsync(instance, currentUserId, currentUserRoles, cancellationToken))
        {
            throw new ForbiddenAppException("FORM_INSTANCE_NOT_AUTHORIZED", "You are not authorized to attach files to this form.");
        }

        if (instance.Status != FormInstanceStatus.Draft)
        {
            throw new ConflictAppException("FORM_NOT_EDITABLE", $"Form instance '{formInstanceId}' is {instance.Status} and can no longer accept attachments.");
        }

        ValidateUpload(fileName, contentType, size);

        var sanitizedFileName = SanitizeFileName(fileName);
        var storageKey = $"form-instances/{formInstanceId}/{Guid.NewGuid():N}-{sanitizedFileName}";

        await using var buffered = new MemoryStream();
        await content.CopyToAsync(buffered, cancellationToken);
        buffered.Position = 0;

        using var sha256 = SHA256.Create();
        var hash = Convert.ToHexString(await sha256.ComputeHashAsync(buffered, cancellationToken));
        buffered.Position = 0;

        try
        {
            await _storage.SaveAsync(storageKey, buffered, contentType, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attachment upload failed writing to object storage for form instance {FormInstanceId}, key {StorageKey}.", formInstanceId, storageKey);
            throw new ConflictAppException("ATTACHMENT_STORAGE_UNAVAILABLE", "The file could not be stored. Please try again.");
        }

        var attachment = new Attachment
        {
            FormInstanceId = formInstanceId,
            FileName = sanitizedFileName,
            ContentType = contentType,
            Size = size,
            StorageKey = storageKey,
            Hash = hash,
            UploadedByUserId = currentUserId,
        };
        _db.Attachments.Add(attachment);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The object was already written to MinIO above, but the metadata row it needs to be
            // reachable through never committed — best-effort delete it so it doesn't sit as a
            // permanent orphan. A cleanup failure is logged, never silently swallowed into a
            // misleading "upload succeeded" — the caller still gets the original failure.
            _logger.LogError(ex, "Attachment metadata save failed for form instance {FormInstanceId}; attempting best-effort cleanup of orphaned object {StorageKey}.", formInstanceId, storageKey);
            try
            {
                await _storage.DeleteAsync(storageKey, cancellationToken);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Best-effort cleanup of orphaned object {StorageKey} also failed after a metadata save failure; the object remains in storage with no database row.", storageKey);
            }

            throw new ConflictAppException("ATTACHMENT_UPLOAD_FAILED", "The file could not be saved. Please try again.");
        }

        // Audited only after the metadata row has actually committed — AuditLog must never claim
        // a successful upload that didn't happen.
        await _auditService.LogAsync(AuditActions.AttachmentUploaded, nameof(Attachment), attachment.Id.ToString(), newValue: new { formInstanceId, attachment.FileName, attachment.Size }, cancellationToken: cancellationToken);

        return ToDto(attachment);
    }

    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid formInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var instance = await _db.FormInstances.SingleOrDefaultAsync(f => f.Id == formInstanceId, cancellationToken)
            ?? throw new NotFoundAppException("FORM_INSTANCE_NOT_FOUND", $"Form instance '{formInstanceId}' was not found.");

        if (!await _authorization.CanViewAsync(instance, currentUserId, currentUserRoles, cancellationToken))
        {
            throw new ForbiddenAppException("FORM_INSTANCE_NOT_AUTHORIZED", "You are not authorized to view this form's attachments.");
        }

        var attachments = await _db.Attachments.AsNoTracking()
            .Where(a => a.FormInstanceId == formInstanceId)
            .OrderBy(a => a.UploadedAt)
            .ToListAsync(cancellationToken);

        return attachments.Select(ToDto).ToList();
    }

    public async Task<(AttachmentDto Metadata, Stream Content)?> DownloadAsync(Guid attachmentId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var attachment = await _db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
        if (attachment is null)
        {
            return null;
        }

        var instance = await _db.FormInstances.SingleAsync(f => f.Id == attachment.FormInstanceId, cancellationToken);
        if (!await _authorization.CanViewAsync(instance, currentUserId, currentUserRoles, cancellationToken))
        {
            throw new ForbiddenAppException("FORM_INSTANCE_NOT_AUTHORIZED", "You are not authorized to access this attachment.");
        }

        try
        {
            var stream = await _storage.OpenReadAsync(attachment.StorageKey, cancellationToken);
            return (ToDto(attachment), stream);
        }
        catch (Exception ex)
        {
            // A dangling row (its object already removed from storage some other way, or the
            // store is simply unreachable) must never leak a raw storage exception to the client.
            _logger.LogError(ex, "Attachment content could not be read from object storage for attachment {AttachmentId}, key {StorageKey}.", attachmentId, attachment.StorageKey);
            throw new ConflictAppException("ATTACHMENT_CONTENT_UNAVAILABLE", "This attachment's file content could not be retrieved.");
        }
    }

    public async Task DeleteAsync(Guid attachmentId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var attachment = await _db.Attachments.SingleOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            ?? throw new NotFoundAppException("ATTACHMENT_NOT_FOUND", $"Attachment '{attachmentId}' was not found.");

        var instance = await _db.FormInstances.SingleAsync(f => f.Id == attachment.FormInstanceId, cancellationToken);
        if (!await _authorization.CanEditAsync(instance, currentUserId, currentUserRoles, cancellationToken))
        {
            throw new ForbiddenAppException("FORM_INSTANCE_NOT_AUTHORIZED", "You are not authorized to delete this attachment.");
        }

        if (instance.Status != FormInstanceStatus.Draft)
        {
            throw new ConflictAppException("FORM_NOT_EDITABLE", $"Form instance '{instance.Id}' is {instance.Status}; attachments can no longer be removed.");
        }

        try
        {
            await _storage.DeleteAsync(attachment.StorageKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attachment delete failed removing object storage for attachment {AttachmentId}, key {StorageKey}.", attachmentId, attachment.StorageKey);
            throw new ConflictAppException("ATTACHMENT_STORAGE_UNAVAILABLE", "The file could not be deleted. Please try again.");
        }

        _db.Attachments.Remove(attachment);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The object is already gone from storage but the metadata row didn't commit its own
            // removal — a genuinely dangling row (points at a now-missing object). There is no
            // object left to restore, so this cannot be silently compensated; log it clearly so
            // it's discoverable, and fail the request rather than reporting success.
            _logger.LogError(ex, "Attachment {AttachmentId} was removed from object storage but its metadata row failed to delete — it now points at a missing object.", attachmentId);
            throw new ConflictAppException("ATTACHMENT_DELETE_FAILED", "The file was removed from storage but the attachment record could not be updated. Please contact an administrator.");
        }

        // Audited only after the metadata row has actually committed its removal.
        await _auditService.LogAsync(AuditActions.AttachmentDeleted, nameof(Attachment), attachment.Id.ToString(), oldValue: new { attachment.FormInstanceId, attachment.FileName }, cancellationToken: cancellationToken);
    }

    private static void ValidateUpload(string fileName, string contentType, long size)
    {
        if (size <= 0 || size > MaxFileSizeBytes)
        {
            throw new BadRequestAppException("ATTACHMENT_TOO_LARGE", $"File size must be between 1 byte and {MaxFileSizeBytes / (1024 * 1024)} MB.");
        }

        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
        {
            // Skill.md §29: "prevent path traversal."
            throw new BadRequestAppException("ATTACHMENT_INVALID_FILENAME", "File name is missing or contains invalid path characters.");
        }

        if (!AllowedExtensionsByContentType.TryGetValue(contentType, out var allowedExtensions))
        {
            throw new BadRequestAppException("ATTACHMENT_TYPE_NOT_ALLOWED", $"Content type '{contentType}' is not permitted.");
        }

        var extension = Path.GetExtension(fileName);
        if (!allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new BadRequestAppException("ATTACHMENT_TYPE_NOT_ALLOWED", $"File extension '{extension}' does not match declared content type '{contentType}'.");
        }
    }

    private static string SanitizeFileName(string fileName) => Path.GetFileName(fileName);

    private static AttachmentDto ToDto(Attachment attachment) =>
        new(attachment.Id, attachment.FormInstanceId, attachment.FileName, attachment.ContentType, attachment.Size, attachment.Hash, attachment.UploadedByUserId, attachment.UploadedAt);
}
