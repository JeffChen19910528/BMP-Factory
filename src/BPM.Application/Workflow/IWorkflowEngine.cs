using BPM.Application.Processes;

namespace BPM.Application.Workflow;

// The one place mutating workflow operations are allowed to happen (Skill.md §15: "Do not put
// workflow execution logic inside Controllers"). Controllers call this through the Application
// layer; the concrete implementation (BPM.Workflow.Engine.WorkflowEngine) is the only thing
// allowed to touch ProcessInstance/TaskInstance/AuditLog together in one transaction.
public interface IWorkflowEngine
{
    // Validates and publishes the definition's latest Draft version. Throws NotFoundAppException
    // if the definition or a draft version doesn't exist, ValidationAppException if the
    // definition graph fails validation (Skill.md §12, §30).
    Task<ProcessVersionDto> PublishVersionAsync(Guid processDefinitionId, Guid publishedBy, CancellationToken cancellationToken = default);

    // Throws NotFoundAppException if no Published version exists for the given key
    // (Skill.md §13: "A process must NOT start from a Draft version").
    Task<ProcessInstanceDto> StartProcessAsync(StartProcessRequest request, Guid initiatorId, CancellationToken cancellationToken = default);

    // Throws NotFoundAppException (task doesn't exist), ForbiddenAppException (caller is not the
    // assignee / doesn't hold the assigned role), or ConflictAppException (task already
    // completed, or a concurrent completion won the race — Skill.md §19). Only for plain
    // UserTask nodes — an ApprovalTask must go through Approve/Reject/Return below instead
    // (BadRequestAppException TASK_NOT_APPROVAL_TASK / TASK_IS_APPROVAL_TASK guards each side).
    Task<TaskDto> CompleteTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    // Approval Engine actions (Phase 3, Skill.md §14-§19) — only valid for ApprovalTask tasks.
    // Each resolves the caller against their ApprovalAssignment (direct match or an active
    // delegation), evaluates the node's ApprovalPolicy, and — when the policy's outcome is
    // decided — advances the workflow via the same transition logic CompleteTaskAsync uses, all
    // within the one SaveChangesAsync that closes out the method (Skill.md §23).
    Task<TaskDto> ApproveTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    // Rejecting terminates the whole ProcessInstance as Rejected (Skill.md §14/§17) — distinct
    // from Return, which rewinds one step and keeps the process Running.
    Task<TaskDto> RejectTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    // Sends the task back to the node's configured return target (only PreviousUserTask is
    // supported as of Phase 3 — Skill.md §16) and creates a fresh task there; the process stays
    // Running throughout.
    Task<TaskDto> ReturnTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    // Adds delegateToUserId as an additional authorized actor on the caller's own assignment —
    // the original assignee (currentUserId) remains recorded and, in this implementation, remains
    // authorized too (Skill.md §17 rule 1 only requires the original stay *recorded*, not that
    // they lose access — see BPM.Workflow.Engine.ApprovalEngine's doc comment for the reasoning).
    Task<TaskDto> DelegateTaskAsync(Guid taskId, Guid currentUserId, Guid delegateToUserId, CancellationToken cancellationToken = default);

    // Permanently reassigns the caller's own assignment to newUserId (Skill.md §18) — the caller
    // stops being the assignee; contrast with Delegate, which is additive and temporary. Only the
    // current assignee may transfer their own slot (see ApprovalEngine's doc comment for why
    // there's no admin override here).
    Task<TaskDto> TransferTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, Guid newUserId, string? reason, CancellationToken cancellationToken = default);

    // Administrator-only (Skill.md §19's "must obey the current approval policy" is interpreted
    // here as a least-privilege default — see ApprovalEngine's doc comment): adds a new
    // ApprovalAssignment to a still-Pending ApprovalInstance without touching the ProcessVersion.
    Task<TaskDto> AddApproverAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, Guid newApproverUserId, CancellationToken cancellationToken = default);
}
