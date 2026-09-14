using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public enum ProcessInstanceStatus
{
    Running,
    Completed,
    Rejected,
    Cancelled,
    Suspended,
    Failed,
}

// Runtime-side entity (Skill.md §2.2). Pinned to the exact ProcessVersion it started with
// (Skill.md §2.3) so publishing a new version never affects instances already running on an
// older one. Current position in the graph is derived from its non-terminal TaskInstances rather
// than stored redundantly here — Phase 2 workflows are strictly sequential (no parallel
// gateways yet), so there is at most one such task at a time.
public class ProcessInstance : AuditableEntity
{
    public Guid ProcessDefinitionId { get; set; }
    public ProcessDefinition? ProcessDefinition { get; set; }

    public Guid ProcessVersionId { get; set; }
    public ProcessVersion? ProcessVersion { get; set; }

    public string? BusinessKey { get; set; }
    public Guid InitiatorId { get; set; }
    public ProcessInstanceStatus Status { get; set; } = ProcessInstanceStatus.Running;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public ICollection<TaskInstance> Tasks { get; set; } = new List<TaskInstance>();
}
