using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public enum ApprovalPolicy
{
    Sequential,
    All,
    AnyOne,
}

public enum ApprovalInstanceStatus
{
    Pending,
    Approved,
    Rejected,
    Returned,
    Cancelled,
}

// Runtime approval state for one ApprovalTask TaskInstance (Skill.md Phase 3 §12). Kept as its
// own entity rather than overloading TaskInstance — a TaskInstance is "one unit of work"
// (Skill.md §15's UserTask concept), while an ApprovalTask's single unit of work can require
// input from many distinct people under a policy (Sequential/All/AnyOne). One TaskInstance has
// at most one ApprovalInstance (1:1), created only for ApprovalTask nodes; plain UserTask nodes
// have none and keep using TaskInstance.AssigneeId/AssigneeRole exactly as in Phase 2.
public class ApprovalInstance : AuditableEntity
{
    public Guid TaskInstanceId { get; set; }
    public TaskInstance? TaskInstance { get; set; }

    public ApprovalPolicy Policy { get; set; }

    // Total assignments required to reach Approved: for All/Sequential, every resolved approver;
    // for AnyOne, always 1 (regardless of how many candidates were resolved).
    public int RequiredCount { get; set; }
    public int ApprovedCount { get; set; }
    public int RejectedCount { get; set; }

    public ApprovalInstanceStatus Status { get; set; } = ApprovalInstanceStatus.Pending;

    public ICollection<ApprovalAssignment> Assignments { get; set; } = new List<ApprovalAssignment>();
}
