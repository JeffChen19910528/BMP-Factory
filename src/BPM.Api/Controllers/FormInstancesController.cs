using BPM.Application.Common;
using BPM.Application.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/form-instances")]
public class FormInstancesController : ControllerBase
{
    private const long MaxUploadSizeBytes = 25 * 1024 * 1024;

    private readonly IFormInstanceQueryService _queryService;
    private readonly IFormEngine _formEngine;
    private readonly IAttachmentService _attachmentService;
    private readonly ICurrentUserService _currentUser;

    public FormInstancesController(IFormInstanceQueryService queryService, IFormEngine formEngine, IAttachmentService attachmentService, ICurrentUserService currentUser)
    {
        _queryService = queryService;
        _formEngine = formEngine;
        _attachmentService = attachmentService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FormInstanceDto>>> GetByProcessInstance([FromQuery] Guid processInstanceId, CancellationToken cancellationToken) =>
        Ok(await _queryService.GetByProcessInstanceAsync(processInstanceId, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<FormInstanceDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var instance = await _queryService.GetByIdAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken);
        return instance is null ? NotFound() : Ok(instance);
    }

    [HttpGet("{id:guid}/data")]
    public async Task<ActionResult<FormDataDto>> GetData(Guid id, CancellationToken cancellationToken)
    {
        var data = await _queryService.GetDataAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken);
        return data is null ? NotFound() : Ok(data);
    }

    [HttpPost]
    public async Task<ActionResult<FormInstanceDto>> Create(CreateFormInstanceRequest request, CancellationToken cancellationToken)
    {
        var instance = await _formEngine.CreateInstanceAsync(request, _currentUser.RequireUserId(), cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = instance.Id }, instance);
    }

    [HttpPut("{id:guid}/data")]
    public async Task<ActionResult<FormDataDto>> SaveData(Guid id, UpdateFormDataRequest request, CancellationToken cancellationToken) =>
        Ok(await _formEngine.SaveDataAsync(id, request, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken));

    [HttpPost("{id:guid}/submit")]
    public async Task<ActionResult<FormInstanceDto>> Submit(Guid id, CancellationToken cancellationToken) =>
        Ok(await _formEngine.SubmitAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<FormInstanceDto>> Cancel(Guid id, CancellationToken cancellationToken) =>
        Ok(await _formEngine.CancelAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken));

    [HttpGet("{id:guid}/attachments")]
    public async Task<ActionResult<IReadOnlyList<AttachmentDto>>> ListAttachments(Guid id, CancellationToken cancellationToken) =>
        Ok(await _attachmentService.ListAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken));

    [HttpPost("{id:guid}/attachments")]
    [RequestSizeLimit(MaxUploadSizeBytes)]
    public async Task<ActionResult<AttachmentDto>> UploadAttachment(Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var attachment = await _attachmentService.UploadAsync(id, file.FileName, file.ContentType, stream, file.Length, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken);
        return Ok(attachment);
    }
}
