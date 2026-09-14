using BPM.Domain.Common;

namespace BPM.Domain.Entities;

// Skill.md §15 lists Pending/InProgress/Completed/Rejected/Returned/Cancelled/Expired as the
// full target state machine. Phase 2 only implements Pending/InProgress/Completed/Cancelled
// (per the Phase 2 spec); the remaining values are reserved so the enum doesn't need to change
// shape again when Reject/Return/SLA-expiry land in later phases.
public enum TaskInstanceStatus
{
    Pending,
    InProgress,
    Completed,
    Rejected,
    Returned,
    Cancelled,
    Expired,
}

// Runtime-side entity. NodeId/NodeName are a snapshot of the owning ProcessVersion's
// DefinitionJson at the moment this task was created — not a foreign key, since nodes are not a
// separate table (Skill.md §11: the graph lives entirely inside the immutable
// ProcessVersion.DefinitionJson blob). Snapshotting avoids re-parsing JSON just to render a task
// list, and is safe because the owning version can never change after publish.
public class TaskInstance : AuditableEntity
{
    public Guid ProcessInstanceId { get; set; }
    public ProcessInstance? ProcessInstance { get; set; }

    public string NodeId { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;

    // Exactly one of these is set, matching the node's WorkflowAssignment.Type at creation time.
    public Guid? AssigneeId { get; set; }
    public string? AssigneeRole { get; set; }

    public TaskInstanceStatus Status { get; set; } = TaskInstanceStatus.Pending;

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? DueAt { get; set; }
}
