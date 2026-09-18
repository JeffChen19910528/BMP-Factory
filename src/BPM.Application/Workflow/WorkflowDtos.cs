using BPM.Application.Common;
using BPM.Application.Sla;
using BPM.Domain.Entities;

namespace BPM.Application.Workflow;

public record ProcessInstanceDto(
    Guid Id,
    Guid ProcessDefinitionId,
    Guid ProcessVersionId,
    string? BusinessKey,
    Guid InitiatorId,
    ProcessInstanceStatus Status,
    DateTime StartedAt,
    DateTime? CompletedAt);

public record StartProcessRequest(string ProcessDefinitionKey, string? BusinessKey);

// Phase 8 — optional governance note supplied at publish time (Part 6). ChangeReason is nullable
// end to end (this request, ProcessVersion.ChangeReason, ProcessVersionDto.ChangeReason) — a
// caller may omit it entirely.
public record PublishProcessRequest(string? ChangeReason);

public record TaskDto(
    Guid Id,
    Guid ProcessInstanceId,
    string NodeId,
    string NodeName,
    Guid? AssigneeId,
    string? AssigneeRole,
    TaskInstanceStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? DueAt,
    ApprovalSummaryDto? Approval = null,
    TaskSlaDto? Sla = null);

// Phase 5.5.1 — Approvals Worklist. A read-only projection over the same data GetMyTasksAsync
// already scopes and returns, widened with the process/applicant context a worklist row needs to
// be useful (process name/key, applicant id) that TaskDto alone doesn't carry — never a second
// domain model, just a wider DTO built from the existing TaskInstance/ProcessInstance/
// ProcessDefinition/ApprovalInstance rows. Page/Search/Status follow the exact same convention
// ProcessDefinitionQuery already established.
public record ApprovalWorklistQuery(string? Search = null, TaskInstanceStatus? Status = null, int Page = 1, int PageSize = 20);

// Phase 12 — GetMyTasksAsync previously had no pagination at all, fetching every task the caller
// has ever been assignee/role-eligible/approval-participant on, unbounded. The default PageSize
// (200, not the usual 20) is deliberately generous — the goal is closing the "grows forever with
// no limit" risk, not shrinking what a realistic user already sees today; My Tasks has no
// dedicated frontend pagination UI (it relies on Ant Design Table's own client-side paging), so a
// low default would be a visible behavior regression for existing users. The upper bound (200) and
// PagedResult<TaskDto> return shape match every other paginated query in this codebase.
public record MyTasksQuery(int Page = 1, int PageSize = 200);

public record ApprovalWorklistItemDto(
    Guid TaskId,
    Guid ProcessInstanceId,
    string ProcessDefinitionKey,
    string ProcessDefinitionName,
    string TaskName,
    Guid ApplicantId,
    Guid? AssigneeId,
    string? AssigneeRole,
    TaskInstanceStatus TaskStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DueAt,
    ApprovalSummaryDto? Approval);
