using BPM.Domain.Entities;
using BPM.Domain.Forms;

namespace BPM.Application.Forms;

// Mirrors BPM.Application.Workflow.ValidateWorkflowDefinitionRequest — same pattern, a
// FormSchema-shaped sibling for the standalone "validate without publishing" endpoint.
public record ValidateFormSchemaRequest(FormSchema Schema);

public record FormDefinitionDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string? Category,
    FormDefinitionStatus Status,
    Guid? CurrentVersionId,
    DateTime CreatedAt,
    Guid? CreatedBy,
    DateTime? UpdatedAt);

public record CreateFormDefinitionRequest(string Key, string Name, string? Description, string? Category);

// Metadata-only edit (Phase 5.4.1, mirroring Phase 5.2's UpdateProcessDefinitionRequest exactly)
// — Key is deliberately excluded, same reasoning: it's the stable identifier a UserTask's
// FormReference.formDefinitionKey resolves by, never meant to change after creation. Allowed
// regardless of Status since it doesn't touch anything Skill.md calls immutable (only
// FormVersion.SchemaJson is frozen once published).
public record UpdateFormDefinitionRequest(string Name, string? Description, string? Category);

public record FormVersionDto(
    Guid Id,
    Guid FormDefinitionId,
    int VersionNumber,
    FormVersionStatus Status,
    FormSchema Schema,
    DateTime CreatedAt,
    Guid? CreatedBy,
    DateTime? PublishedAt,
    Guid? PublishedBy,
    // Base64-encoded AuditableEntity.RowVersion — same optimistic-concurrency convention Phase
    // 5.3.2 added to ProcessVersionDto (and FormEngine.SaveDataAsync already used for FormData).
    // Echo back as UpdateFormVersionRequest.ExpectedVersion to save; a stale one is rejected with
    // 409 FORM_VERSION_CONCURRENCY_CONFLICT rather than silently overwriting a concurrent edit.
    string RowVersion);

public record CreateFormVersionRequest(FormSchema Schema);

// Save Draft — updates an existing Draft version's SchemaJson in place. Rejected with
// ConflictAppException if the target version is already Published (immutable once published,
// same rule ProcessVersion follows) — a distinct operation from CreateVersionAsync, which always
// creates a new version row. ExpectedVersion is the RowVersion the client last read
// (FormVersionDto.RowVersion).
public record UpdateFormVersionRequest(FormSchema Schema, string ExpectedVersion);

public interface IFormDefinitionService
{
    Task<IReadOnlyList<FormDefinitionDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<FormDefinitionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<FormDefinitionDto> CreateAsync(CreateFormDefinitionRequest request, CancellationToken cancellationToken = default);
    Task<FormDefinitionDto> UpdateAsync(Guid id, UpdateFormDefinitionRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FormVersionDto>> GetVersionsAsync(Guid formDefinitionId, CancellationToken cancellationToken = default);
    Task<FormVersionDto> CreateVersionAsync(Guid formDefinitionId, CreateFormVersionRequest request, CancellationToken cancellationToken = default);
    Task<FormVersionDto> UpdateVersionAsync(Guid formDefinitionId, Guid versionId, UpdateFormVersionRequest request, CancellationToken cancellationToken = default);
}
