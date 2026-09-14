using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public enum FormDefinitionStatus
{
    Draft,
    Published,
    Suspended,
    Archived,
}

// Definition-side entity, mirroring ProcessDefinition's shape exactly (Skill.md Phase 4 §5) — a
// FormDefinition describes what a form looks like, never a specific submission's data.
public class FormDefinition : AuditableEntity
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public FormDefinitionStatus Status { get; set; } = FormDefinitionStatus.Draft;
    public Guid? CurrentVersionId { get; set; }

    public ICollection<FormVersion> Versions { get; set; } = new List<FormVersion>();
}
