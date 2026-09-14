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

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TaskDto>>> GetMyTasks(CancellationToken cancellationToken) =>
        Ok(await _queryService.GetMyTasksAsync(_currentUser.UserId!.Value, _currentUser.Roles, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TaskDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var task = await _queryService.GetByIdAsync(id, cancellationToken);
        return task is null ? NotFound() : Ok(task);
    }

    [HttpPost("{id:guid}/complete")]
    public async Task<ActionResult<TaskDto>> Complete(Guid id, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId!.Value;
        return Ok(await _workflowEngine.CompleteTaskAsync(id, userId, _currentUser.Roles, cancellationToken));
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<TaskDto>> Approve(Guid id, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.ApproveTaskAsync(id, _currentUser.UserId!.Value, _currentUser.Roles, cancellationToken));

    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<TaskDto>> Reject(Guid id, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.RejectTaskAsync(id, _currentUser.UserId!.Value, _currentUser.Roles, cancellationToken));

    [HttpPost("{id:guid}/return")]
    public async Task<ActionResult<TaskDto>> Return(Guid id, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.ReturnTaskAsync(id, _currentUser.UserId!.Value, _currentUser.Roles, cancellationToken));

    [HttpPost("{id:guid}/delegate")]
    public async Task<ActionResult<TaskDto>> Delegate(Guid id, DelegateTaskRequest request, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.DelegateTaskAsync(id, _currentUser.UserId!.Value, request.DelegateToUserId, cancellationToken));

    [HttpPost("{id:guid}/transfer")]
    public async Task<ActionResult<TaskDto>> Transfer(Guid id, TransferTaskRequest request, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.TransferTaskAsync(id, _currentUser.UserId!.Value, _currentUser.Roles, request.NewUserId, request.Reason, cancellationToken));

    [HttpPost("{id:guid}/approvers")]
    public async Task<ActionResult<TaskDto>> AddApprover(Guid id, AddApproverRequest request, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.AddApproverAsync(id, _currentUser.UserId!.Value, _currentUser.Roles, request.UserId, cancellationToken));
}
