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
    // completed, or a concurrent completion won the race — Skill.md §19).
    Task<TaskDto> CompleteTaskAsync(Guid taskId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);
}
