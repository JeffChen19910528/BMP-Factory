using BPM.Domain.Common;

namespace BPM.Domain.Entities;

// Metadata only (Skill.md Phase 4 §28) — the binary lives in object storage (MinIO/S3-compatible)
// at StorageKey, never in Postgres. Hash lets a client verify the upload wasn't corrupted/tampered
// with; ContentType/FileName here are what the *server* recorded at upload time (trusted), not
// necessarily what the client's browser reported for the same file when downloading later.
public class Attachment : AuditableEntity
{
    public Guid FormInstanceId { get; set; }
    public FormInstance? FormInstance { get; set; }

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;

    public Guid UploadedByUserId { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
