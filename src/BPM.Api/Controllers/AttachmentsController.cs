using BPM.Application.Common;
using BPM.Application.Forms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BPM.Api.Controllers;

// Split from FormInstancesController: once you have an attachment id, you don't need its owning
// FormInstance's id in the URL too — same reasoning TasksController's flat /api/tasks/{id} uses
// rather than nesting under a process instance.
[ApiController]
[Authorize]
[Route("api/attachments")]
public class AttachmentsController : ControllerBase
{
    private readonly IAttachmentService _attachmentService;
    private readonly ICurrentUserService _currentUser;

    public AttachmentsController(IAttachmentService attachmentService, ICurrentUserService currentUser)
    {
        _attachmentService = attachmentService;
        _currentUser = currentUser;
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var result = await _attachmentService.DownloadAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken);
        if (result is null)
        {
            return NotFound();
        }

        var (metadata, content) = result.Value;
        return File(content, metadata.ContentType, metadata.FileName);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _attachmentService.DeleteAsync(id, _currentUser.RequireUserId(), _currentUser.Roles, cancellationToken);
        return NoContent();
    }
}
