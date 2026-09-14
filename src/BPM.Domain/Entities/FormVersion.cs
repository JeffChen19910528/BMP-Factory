using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public enum FormVersionStatus
{
    Draft,
    Published,
}

// SchemaJson holds the full field list (BPM.Domain.Forms.FormSchema, serialized). Once Status is
// Published the row must never be mutated again (Skill.md Phase 4 §13), mirroring
// ProcessVersion's immutability rule exactly — enforced in the application layer, same reasoning
// as ProcessVersion (see its doc comment).
public class FormVersion : AuditableEntity
{
    public Guid FormDefinitionId { get; set; }
    public FormDefinition? FormDefinition { get; set; }

    public int VersionNumber { get; set; }
    public string SchemaJson { get; set; } = string.Empty;
    public FormVersionStatus Status { get; set; } = FormVersionStatus.Draft;

    public Guid? PublishedBy { get; set; }
    public DateTime? PublishedAt { get; set; }
}
