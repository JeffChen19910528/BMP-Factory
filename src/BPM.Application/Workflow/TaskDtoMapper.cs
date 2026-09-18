using BPM.Application.Sla;
using BPM.Domain.Entities;

namespace BPM.Application.Workflow;

// Shared TaskInstance/ApprovalInstance -> DTO mapping. Lives in the Application layer (which
// already depends on Domain) so both BPM.Infrastructure's TaskQueryService and BPM.Workflow's
// ApprovalEngine can use the same projection without Infrastructure having to depend on Workflow
// (the dependency only runs the other way — see BPM.Workflow.csproj).
public static class TaskDtoMapper
{
    // Phase 6.3: `sla` is optional (defaults to null) — ApprovalEngine's own action responses
    // (approve/reject/return/etc., via SaveAndReturnAsync) don't fetch it, since the frontend
    // refetches Task Detail after any action anyway (the established React Query invalidation
    // pattern); only the read paths (TaskQueryService.LoadTasksAsync/GetByIdAsync) pass one.
    public static TaskDto BuildDto(TaskInstance task, ApprovalInstance? approval, TaskSla? sla = null)
    {
        var summary = BuildApprovalSummary(approval);
        var slaDto = sla is null ? null : new TaskSlaDto(sla.StartedAt, sla.WarningAt, sla.DueAt, sla.CompletedAt, sla.Status);
        return new TaskDto(task.Id, task.ProcessInstanceId, task.NodeId, task.NodeName, task.AssigneeId, task.AssigneeRole, task.Status, task.CreatedAt, task.StartedAt, task.CompletedAt, task.DueAt, summary, slaDto);
    }

    // Extracted for Phase 5.5.1's ApprovalWorklistItemDto, which needs the same approval-progress
    // projection but a differently-shaped surrounding DTO — one calculation, two callers, not two
    // independent approval-progress calculations.
    public static ApprovalSummaryDto? BuildApprovalSummary(ApprovalInstance? approval) =>
        approval is null
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
}
