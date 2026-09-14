using BPM.Application.Common;
using BPM.Application.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/process-instances")]
public class ProcessInstancesController : ControllerBase
{
    private readonly IProcessInstanceQueryService _queryService;
    private readonly IWorkflowEngine _workflowEngine;
    private readonly ICurrentUserService _currentUser;

    public ProcessInstancesController(IProcessInstanceQueryService queryService, IWorkflowEngine workflowEngine, ICurrentUserService currentUser)
    {
        _queryService = queryService;
        _workflowEngine = workflowEngine;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProcessInstanceDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _queryService.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProcessInstanceDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var instance = await _queryService.GetByIdAsync(id, cancellationToken);
        return instance is null ? NotFound() : Ok(instance);
    }

    [HttpPost]
    public async Task<ActionResult<ProcessInstanceDto>> Start(StartProcessRequest request, CancellationToken cancellationToken)
    {
        var initiatorId = _currentUser.UserId!.Value;
        var instance = await _workflowEngine.StartProcessAsync(request, initiatorId, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = instance.Id }, instance);
    }
}
