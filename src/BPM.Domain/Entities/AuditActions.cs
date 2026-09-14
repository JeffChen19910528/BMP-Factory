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
}
