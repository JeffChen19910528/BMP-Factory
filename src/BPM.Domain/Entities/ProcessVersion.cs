using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public enum ProcessVersionStatus
{
    Draft,
    Published,
}

// DefinitionJson holds the full node/transition graph (BPM.Domain.Workflow.WorkflowDefinition,
// serialized) for this version. Once Status is Published the row must never be mutated again
// (Skill.md §2.3, §8) — enforced in the application layer, not by the database, since Postgres
// has no generic "freeze this row" constraint; see WorkflowEngine / ProcessVersionService.
public class ProcessVersion : AuditableEntity
{
    public Guid ProcessDefinitionId { get; set; }
    public ProcessDefinition? ProcessDefinition { get; set; }

    public int VersionNumber { get; set; }
    public string DefinitionJson { get; set; } = string.Empty;
    public ProcessVersionStatus Status { get; set; } = ProcessVersionStatus.Draft;

    public Guid? PublishedBy { get; set; }
    public DateTime? PublishedAt { get; set; }

    // Phase 8 — why this version was published (a free-text governance note, e.g. "Added Legal
    // approval step per policy update"), supplied optionally at publish time. Never set/changed
    // afterward — a Published version is immutable, and ChangeReason is part of that same frozen
    // record, not a separately-editable field. NULL for every version published before Phase 8
    // and for any publish where the caller didn't supply one.
    public string? ChangeReason { get; set; }
}
