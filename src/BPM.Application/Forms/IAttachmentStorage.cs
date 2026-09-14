namespace BPM.Application.Forms;

// Object storage abstraction (Skill.md Phase 4 §28: "do not store large files directly in
// PostgreSQL"). The Attachment entity holds only metadata; the bytes live wherever this points —
// MinIO in this repo's docker-compose, any S3-compatible store in production.
public interface IAttachmentStorage
{
    Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
}
