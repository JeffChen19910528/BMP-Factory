using BPM.Domain.Entities;
using BPM.Domain.Forms;

namespace BPM.Application.Forms;

public record FormDefinitionDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string? Category,
    FormDefinitionStatus Status,
    Guid? CurrentVersionId);

public record CreateFormDefinitionRequest(string Key, string Name, string? Description, string? Category);

public record FormVersionDto(
    Guid Id,
    Guid FormDefinitionId,
    int VersionNumber,
    FormVersionStatus Status,
    FormSchema Schema,
    DateTime? PublishedAt);

public record CreateFormVersionRequest(FormSchema Schema);

public interface IFormDefinitionService
{
    Task<IReadOnlyList<FormDefinitionDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<FormDefinitionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<FormDefinitionDto> CreateAsync(CreateFormDefinitionRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FormVersionDto>> GetVersionsAsync(Guid formDefinitionId, CancellationToken cancellationToken = default);
    Task<FormVersionDto> CreateVersionAsync(Guid formDefinitionId, CreateFormVersionRequest request, CancellationToken cancellationToken = default);
}
