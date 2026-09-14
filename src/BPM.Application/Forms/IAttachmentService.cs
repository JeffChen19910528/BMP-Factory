namespace BPM.Application.Forms;

// Upload/list/download/delete for a FormInstance's attachments (Skill.md Phase 4 §28/§29).
// Deliberately its own interface rather than folded into IFormEngine — attachments are a
// cross-cutting concern (binary storage + metadata) distinct from form lifecycle/data, and this
// keeps IFormEngine focused on the Draft/Submitted/Locked/Cancelled state machine.
public interface IAttachmentService
{
    // Validates size/filename/content-type server-side (Skill.md §29: "do not trust the
    // client-provided MIME type") and requires edit access to the FormInstance (Skill.md §22).
    Task<AttachmentDto> UploadAsync(Guid formInstanceId, string fileName, string contentType, Stream content, long size, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AttachmentDto>> ListAsync(Guid formInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    // Returns null if the attachment doesn't exist; throws ForbiddenAppException if the caller
    // can't view the owning FormInstance (Skill.md §29: "users must only access attachments
    // belonging to forms they are authorized to access").
    Task<(AttachmentDto Metadata, Stream Content)?> DownloadAsync(Guid attachmentId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid attachmentId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);
}
