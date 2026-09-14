using System.Text.Json;
using BPM.Domain.Entities;

namespace BPM.Application.Forms;

public record FormInstanceDto(
    Guid Id,
    Guid FormDefinitionId,
    Guid FormVersionId,
    Guid? ProcessInstanceId,
    Guid? TaskInstanceId,
    Guid CreatedByUserId,
    FormInstanceStatus Status);

public record CreateFormInstanceRequest(string FormDefinitionKey, Guid? ProcessInstanceId);

// Version is the FormData row's RowVersion, base64-encoded — round-tripped by the client on the
// next UpdateFormDataRequest so the backend can detect a concurrent edit (Skill.md §26).
public record FormDataDto(Guid FormInstanceId, JsonElement Data, string Version);

public record UpdateFormDataRequest(JsonElement Data, string ExpectedVersion);

public record AttachmentDto(
    Guid Id,
    Guid FormInstanceId,
    string FileName,
    string ContentType,
    long Size,
    string Hash,
    Guid UploadedByUserId,
    DateTime UploadedAt);
