using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public enum ApprovalAssignmentStatus
{
    Pending,
    Approved,
    Rejected,
    Returned,
    Cancelled,
}

// One resolved approver's slot within an ApprovalInstance (Skill.md Phase 3 §12).
//
// UserId is the *current* effective assignee — Transfer (Skill.md §18) permanently overwrites it
// (the previous value is preserved only in the AuditLog trail, not on this row, since "the new
// assignee" is definitionally what UserId means going forward). DelegatedToUserId (Skill.md §17)
// is layered on top instead of replacing UserId: it's temporary and additive — both the original
// UserId and the delegate may act while it's set — which is the opposite of Transfer and is why
// the two need separate fields rather than one being a special case of the other.
//
// Order gates Sequential policy: an assignment is actionable only once every assignment with a
// smaller Order on the same ApprovalInstance has already reached Approved. All/AnyOne ignore
// Order for authorization (every assignment is actionable immediately) but still use it to record
// resolution sequence for AddApprover (Skill.md §19) to append after.
public class ApprovalAssignment : AuditableEntity
{
    public Guid ApprovalInstanceId { get; set; }
    public ApprovalInstance? ApprovalInstance { get; set; }

    public Guid UserId { get; set; }
    public Guid? DelegatedToUserId { get; set; }
    public int Order { get; set; }

    public ApprovalAssignmentStatus Status { get; set; } = ApprovalAssignmentStatus.Pending;

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
