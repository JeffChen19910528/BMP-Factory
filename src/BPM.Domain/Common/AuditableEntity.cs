namespace BPM.Domain.Common;

public abstract class AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Multi-tenant preparation (Skill.md §43) — unused in single-tenant v1, reserved for future use.
    public Guid TenantId { get; set; } = Guid.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    // Optimistic concurrency token (see Skill.md §33 Concurrency Control).
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
