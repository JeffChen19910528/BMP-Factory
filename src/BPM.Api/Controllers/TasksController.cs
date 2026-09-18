using BPM.Application.Common;
using BPM.Application.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/tasks")]
public class TasksController : ControllerBase
{
    private readonly ITaskQueryService _queryService;
    private readonly IWorkflowEngine _workflowEngine;
    private readonly ICurrentUserService _currentUser;

    public TasksController(ITaskQueryService queryService, IWorkflowEngine workflowEngine, ICurrentUserService currentUser)
    {
        _queryService = queryService;
        _workflowEngine = workflowEngine;
        _currentUser = currentUser;
    }

    // Phase 12 — now genuinely paginated (see MyTasksQuery's own doc comment); the response shape
    // changed from a bare array to the same PagedResult<T> envelope every other paginated list in
    // this codebase already uses (e.g. GetApprovalWorklist below). [FromQuery] Page/PageSize are
    // both optional — an unparameterized GET /api/tasks keeps working, now returning up to 200
    // tasks (MyTasksQuery's default) instead of an unbounded number.
    [HttpGet]
    public async Task<ActionResult<PagedResult<TaskDto>>> GetMyTasks([FromQuery] MyTasksQuery query, CancellationToken cancellationToken) =>
        Ok(await _queryService.GetMyTasksAsync(_currentUser.RequireUserId(), _currentUser.Roles, query, cancellationToken));

    // Phase 5.5.1 — Approvals Worklist: a dedicated, paginated/filterable/searchable view over
    // exactly the approval tasks the caller participates in. "approvals" as a literal segment
    // never collides with the `{id:guid}` route below — ASP.NET Core's guid route constraint
    // simply doesn't match a non-guid literal. currentUserId always comes from the authenticated
    // caller (ICurrentUserService), never from a query parameter — a client cannot ask for
    // another user's worklist by passing a different id anywhere in this request.
    [HttpGet("approvals")]
    public async Task<ActionResult<PagedResult<ApprovalWorklistItemDto>>> GetApprovalWorklist([FromQuery] ApprovalWorklistQuery query, CancellationToken cancellationToken) =>
        Ok(await _queryService.GetApprovalWorklistAsync(_currentUser.RequireUserId(), _currentUser.Roles, query, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TaskDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var task = await _queryService.GetByIdAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<ActionResult<TaskDto>> Complete(Guid id, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.CompleteTaskAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken));

    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<TaskDto>> Approve(Guid id, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.ApproveTaskAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<TaskDto>> Reject(Guid id, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.RejectTaskAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken));

    [HttpPost("{id:guid}/return")]
    public async Task<ActionResult<TaskDto>> Return(Guid id, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.ReturnTaskAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken));

    [HttpPost("{id:guid}/delegate")]
    public async Task<ActionResult<TaskDto>> Delegate(Guid id, DelegateTaskRequest request, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.DelegateTaskAsync(id, _currentUser.RequireUserId(), request.DelegateToUserId, cancellationToken));

    [HttpPost("{id:guid}/transfer")]
    public async Task<ActionResult<TaskDto>> Transfer(Guid id, TransferTaskRequest request, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.TransferTaskAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, request.NewUserId, request.Reason, cancellationToken));

    [HttpPost("{id:guid}/approvers")]
    public async Task<ActionResult<TaskDto>> AddApprover(Guid id, AddApproverRequest request, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.AddApproverAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, request.UserId, cancellationToken));
}
