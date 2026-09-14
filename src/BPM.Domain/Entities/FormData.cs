using BPM.Domain.Common;

namespace BPM.Domain.Entities;

// Submitted values, kept separate from the schema (Skill.md Phase 4 §12: "Schema != Runtime
// Data"). One row per FormInstance — no per-field columns, since the field set is entirely
// defined by the (immutable) FormVersion this instance points at, not by this table's shape.
// RowVersion (from AuditableEntity) is what makes concurrent-edit detection possible (Skill.md
// §26): a save is a plain optimistic-concurrency update.
public class FormData : AuditableEntity
{
    public Guid FormInstanceId { get; set; }
    public FormInstance? FormInstance { get; set; }

    public string DataJson { get; set; } = "{}";
}
