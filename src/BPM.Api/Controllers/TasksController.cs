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
}
