using BPM.Domain.Entities;

namespace BPM.Application.Workflow;

// Shared TaskInstance/ApprovalInstance -> DTO mapping. Lives in the Application layer (which
// already depends on Domain) so both BPM.Infrastructure's TaskQueryService and BPM.Workflow's
// ApprovalEngine can use the same projection without Infrastructure having to depend on Workflow
// (the dependency only runs the other way — see BPM.Workflow.csproj).
public static class TaskDtoMapper
{
    public static TaskDto BuildDto(TaskInstance task, ApprovalInstance? approval)
    {
        ApprovalSummaryDto? summary = approval is null
            ? null
            : new ApprovalSummaryDto(
                approval.Policy,
                approval.Status,
                approval.RequiredCount,
                approval.ApprovedCount,
                approval.RejectedCount,
                approval.Assignments
                    .OrderBy(a => a.Order)
                    .Select(a => new ApprovalAssignmentDto(a.Id, a.UserId, a.DelegatedToUserId, a.Order, a.Status, a.AssignedAt, a.CompletedAt))
                    .ToList());

        return new TaskDto(task.Id, task.ProcessInstanceId, task.NodeId, task.NodeName, task.AssigneeId, task.AssigneeRole, task.Status, task.CreatedAt, task.StartedAt, task.CompletedAt, task.DueAt, summary);
    }
}
