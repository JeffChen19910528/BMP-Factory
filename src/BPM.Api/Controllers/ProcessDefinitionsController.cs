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
    public async Task<ActionResult<PagedResult<ProcessDefinitionDto>>> GetAll([FromQuery] ProcessDefinitionQuery query, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.GetAllAsync(query, cancellationToken));

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

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ProcessDefinitionDto>> Update(Guid id, UpdateProcessDefinitionRequest request, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.UpdateAsync(id, request, cancellationToken));

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<IReadOnlyList<ProcessVersionDto>>> GetVersions(Guid id, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.GetVersionsAsync(id, cancellationToken));

    [HttpPost("{id:guid}/versions")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ProcessVersionDto>> CreateVersion(Guid id, CreateProcessVersionRequest request, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.CreateVersionAsync(id, request, cancellationToken));

    [HttpPut("{id:guid}/versions/{versionId:guid}")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ProcessVersionDto>> UpdateVersion(Guid id, Guid versionId, UpdateProcessVersionRequest request, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.UpdateVersionAsync(id, versionId, request, cancellationToken));

    [HttpPost("{id:guid}/publish")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ProcessVersionDto>> Publish(Guid id, [FromBody] PublishProcessRequest? request, CancellationToken cancellationToken) =>
        Ok(await _workflowEngine.PublishVersionAsync(id, _currentUser.RequireUserId(), request?.ChangeReason, cancellationToken));

    // ---- Phase 8 — Process Governance & Lifecycle ----

    // [Authorize] only (not role-restricted) — SuspendAsync/ArchiveAsync/RestoreAsync themselves
    // enforce Administrator-OR-owner from the loaded entity's own OwnerUserId (Part 3); a bare
    // role attribute can't express "owner of this specific resource," so the check moves into the
    // service, the same place every other resource-scoped authorization decision in this codebase
    // already lives (ProcessInstanceQueryService.GetByIdAsync, TaskQueryService.GetByIdAsync).
    [HttpPost("{id:guid}/suspend")]
    [Authorize]
    public async Task<ActionResult<ProcessDefinitionDto>> Suspend(Guid id, ProcessLifecycleActionRequest request, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.SuspendAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, request, cancellationToken));

    [HttpPost("{id:guid}/archive")]
    [Authorize]
    public async Task<ActionResult<ProcessDefinitionDto>> Archive(Guid id, ProcessLifecycleActionRequest request, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.ArchiveAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, request, cancellationToken));

    [HttpPost("{id:guid}/restore")]
    [Authorize]
    public async Task<ActionResult<ProcessDefinitionDto>> Restore(Guid id, ProcessLifecycleActionRequest request, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.RestoreAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, request, cancellationToken));

    // Administrator-only (Part 3/15) — ownership assignment/change/clear is never something an
    // owner can do to themselves.
    [HttpPost("{id:guid}/owner")]
    [Authorize(Roles = "Administrator")]
    public async Task<ActionResult<ProcessDefinitionDto>> AssignOwner(Guid id, AssignProcessOwnerRequest request, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.AssignOwnerAsync(id, request, cancellationToken));

    // Read-only, any authenticated user — matches GetVersions' own gating; both versions compared
    // must belong to `id` (enforced in CompareVersionsAsync, never trusted from the query string).
    [HttpGet("{id:guid}/versions/compare")]
    public async Task<ActionResult<VersionComparisonResponse>> CompareVersions(Guid id, [FromQuery] Guid fromVersionId, [FromQuery] Guid toVersionId, CancellationToken cancellationToken) =>
        Ok(await _processDefinitionService.CompareVersionsAsync(id, fromVersionId, toVersionId, cancellationToken));

    // Stateless preview validation for the JSON Definition Editor's [Validate] action (frontend
    // spec §10) — runs the same authoritative WorkflowDefinitionValidator PublishVersionAsync
    // uses, but never persists or publishes anything. Always 200: "invalid" is a normal validation
    // outcome to render, not a request failure.
    [HttpPost("validate")]
    [Authorize(Roles = "Administrator")]
    public ActionResult<WorkflowValidationResultDto> Validate(ValidateWorkflowDefinitionRequest request) =>
        Ok(_workflowEngine.ValidateDefinition(request.Definition));
}
