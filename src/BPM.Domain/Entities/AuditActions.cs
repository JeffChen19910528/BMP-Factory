namespace BPM.Domain.Entities;

// Canonical audit action names (Skill.md §26). Use these constants rather than free-text strings
// so audit queries and reporting stay consistent as new actions are added in later phases.
public static class AuditActions
{
    public const string Login = "Login";
    public const string CreateProcess = "CreateProcess";
    public const string ModifyProcess = "ModifyProcess";
    public const string PublishProcess = "PublishProcess";
    public const string StartProcess = "StartProcess";
    public const string AssignTask = "AssignTask";
    public const string Approve = "Approve";
    public const string Reject = "Reject";
    public const string Return = "Return";
    public const string Delegate = "Delegate";
    public const string AddApprover = "AddApprover";
    public const string Cancel = "Cancel";
    public const string Complete = "Complete";
    public const string PermissionChange = "PermissionChange";

    // Workflow engine events (Phase 2, Skill.md §21).
    public const string TaskCreated = "TaskCreated";
    public const string TaskCompleted = "TaskCompleted";
    public const string ProcessCompleted = "ProcessCompleted";
    public const string WorkflowTransition = "WorkflowTransition";

    // Approval engine events (Phase 3, Skill.md §24). Distinct from the generic
    // Approve/Reject/Return/Delegate/AddApprover constants above (reserved from Phase 1's
    // canonical list) so approval-specific audit entries can be filtered unambiguously.
    public const string ApprovalAssigned = "ApprovalAssigned";
    public const string ApprovalApproved = "ApprovalApproved";
    public const string ApprovalRejected = "ApprovalRejected";
    public const string ApprovalReturned = "ApprovalReturned";
    public const string ApprovalDelegated = "ApprovalDelegated";
    public const string ApprovalTransferred = "ApprovalTransferred";
    public const string ApproverAdded = "ApproverAdded";
    public const string ApprovalCompleted = "ApprovalCompleted";

    // Form engine events (Phase 4, Skill.md §25).
    public const string FormCreated = "FormCreated";
    public const string FormVersionCreated = "FormVersionCreated";
    public const string FormVersionPublished = "FormVersionPublished";
    public const string FormInstanceCreated = "FormInstanceCreated";
    public const string FormDataUpdated = "FormDataUpdated";
    public const string FormSubmitted = "FormSubmitted";
    public const string FormLocked = "FormLocked";
    public const string FormCancelled = "FormCancelled";
    public const string AttachmentUploaded = "AttachmentUploaded";
    public const string AttachmentDeleted = "AttachmentDeleted";
}
