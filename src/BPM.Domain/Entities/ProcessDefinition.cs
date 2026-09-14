using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public enum ProcessDefinitionStatus
{
    Draft,
    Published,
    Suspended,
    Archived,
}

// Definition-side entity (Skill.md §2.2): describes what a process looks like, never how a
// specific running instance is progressing. CurrentVersionId points at the ProcessVersion that
// new ProcessInstances are started from; it only ever points at a Published version.
public class ProcessDefinition : AuditableEntity
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public ProcessDefinitionStatus Status { get; set; } = ProcessDefinitionStatus.Draft;
    public Guid? CurrentVersionId { get; set; }

    public ICollection<ProcessVersion> Versions { get; set; } = new List<ProcessVersion>();
}
