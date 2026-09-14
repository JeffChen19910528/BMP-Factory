using BPM.Application.Common;
using BPM.Application.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/form-definitions")]
public class FormDefinitionsController : ControllerBase
{
    private readonly IFormDefinitionService _formDefinitionService;
    private readonly IFormEngine _formEngine;
    private readonly ICurrentUserService _currentUser;

    public FormDefinitionsController(IFormDefinitionService formDefinitionService, IFormEngine formEngine, ICurrentUserService currentUser)
    {
        _formDefinitionService = formDefinitionService;
        _formEngine = formEngine;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FormDefinitionDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _formDefinitionService.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FormDefinitionDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var definition = await _formDefinitionService.GetByIdAsync(id, cancellationToken);
        return definition is null ? NotFound() : Ok(definition);
    }

    [HttpPost]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<FormDefinitionDto>> Create(CreateFormDefinitionRequest request, CancellationToken cancellationToken)
    {
        var definition = await _formDefinitionService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = definition.Id }, definition);
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<IReadOnlyList<FormVersionDto>>> GetVersions(Guid id, CancellationToken cancellationToken) =>
        Ok(await _formDefinitionService.GetVersionsAsync(id, cancellationToken));

    [HttpPost("{id:guid}/versions")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<FormVersionDto>> CreateVersion(Guid id, CreateFormVersionRequest request, CancellationToken cancellationToken) =>
        Ok(await _formDefinitionService.CreateVersionAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/publish")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<FormVersionDto>> Publish(Guid id, CancellationToken cancellationToken) =>
        Ok(await _formEngine.PublishVersionAsync(id, _currentUser.RequireUserId(), cancellationToken));
}
