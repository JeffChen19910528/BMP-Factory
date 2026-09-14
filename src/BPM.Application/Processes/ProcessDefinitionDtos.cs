using BPM.Domain.Entities;
using BPM.Domain.Workflow;

namespace BPM.Application.Processes;

public record ProcessDefinitionDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string? Category,
    ProcessDefinitionStatus Status,
    Guid? CurrentVersionId);

public record CreateProcessDefinitionRequest(string Key, string Name, string? Description, string? Category);

public record ProcessVersionDto(
    Guid Id,
    Guid ProcessDefinitionId,
    int VersionNumber,
    ProcessVersionStatus Status,
    WorkflowDefinition Definition,
    DateTime? PublishedAt);

public record CreateProcessVersionRequest(WorkflowDefinition Definition);

public interface IProcessDefinitionService
{
    Task<IReadOnlyList<ProcessDefinitionDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<ProcessDefinitionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ProcessDefinitionDto> CreateAsync(CreateProcessDefinitionRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProcessVersionDto>> GetVersionsAsync(Guid processDefinitionId, CancellationToken cancellationToken = default);
    Task<ProcessVersionDto> CreateVersionAsync(Guid processDefinitionId, CreateProcessVersionRequest request, CancellationToken cancellationToken = default);
}
