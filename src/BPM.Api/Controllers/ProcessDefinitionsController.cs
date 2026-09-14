using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/process-definitions")]
public class ProcessDefinitionsController : ControllerBase
{
    private readonly IProcessDefinitionService _processDefinitionService;
    private readonly IWorkflowEngine _workflowEngine;
    private readonly ICurrentUserService _currentUser;

    public ProcessDefinitionsController(IProcessDefinitionService processDefinitionService, IWorkflowEngine workflowEngine, ICurrentUserService currentUser)
    {
        _processDefinitionService = processDefinitionService;
        _workflowEngine = workflowEngine;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProcessDefinitionDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProcessDefinitionDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var definition = await _processDefinitionService.GetByIdAsync(id, cancellationToken);
        return definition is null ? NotFound() : Ok(definition);
    }

    [HttpPost]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ProcessDefinitionDto>> Create(CreateProcessDefinitionRequest request, CancellationToken cancellationToken)
    {
        var definition = await _processDefinitionService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = definition.Id }, definition);
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<IReadOnlyList<ProcessVersionDto>>> GetVersions(Guid id, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.GetVersionsAsync(id, cancellationToken));

    [HttpPost("{id:guid}/versions")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ProcessVersionDto>> CreateVersion(Guid id, CreateProcessVersionRequest request, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.CreateVersionAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/publish")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ProcessVersionDto>> Publish(Guid id, CancellationToken cancellationToken)
    {
        var publishedBy = _currentUser.UserId!.Value;
        return Ok(await _workflowEngine.PublishVersionAsync(id, publishedBy, cancellationToken));
    }
}
