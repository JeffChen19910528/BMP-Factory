using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Plain CRUD only (create definition, create a draft version, list/get). Publishing and running
// a process are engine operations — see BPM.Workflow.Engine.WorkflowEngine — and deliberately do
// not live here, to keep this service's dependencies (just the DbContext) free of the workflow
// validator/engine.
public class ProcessDefinitionService : IProcessDefinitionService
{
    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;
    private readonly IValidator<CreateProcessDefinitionRequest> _createDefinitionValidator;
    private readonly IValidator<CreateProcessVersionRequest> _createVersionValidator;

    public ProcessDefinitionService(
        BpmDbContext db,
        IAuditService auditService,
        IValidator<CreateProcessDefinitionRequest> createDefinitionValidator,
        IValidator<CreateProcessVersionRequest> createVersionValidator)
    {
        _db = db;
        _auditService = auditService;
        _createDefinitionValidator = createDefinitionValidator;
        _createVersionValidator = createVersionValidator;
    }

    public async Task<IReadOnlyList<ProcessDefinitionDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.ProcessDefinitions
            .AsNoTracking()
            .Select(p => new ProcessDefinitionDto(p.Id, p.Key, p.Name, p.Description, p.Category, p.Status, p.CurrentVersionId))
            .ToListAsync(cancellationToken);

    public async Task<ProcessDefinitionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _db.ProcessDefinitions
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new ProcessDefinitionDto(p.Id, p.Key, p.Name, p.Description, p.Category, p.Status, p.CurrentVersionId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ProcessDefinitionDto> CreateAsync(CreateProcessDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        await _createDefinitionValidator.ValidateAndThrowAsync(request, cancellationToken);

        var keyTaken = await _db.ProcessDefinitions.AnyAsync(p => p.Key == request.Key, cancellationToken);
        if (keyTaken)
        {
            throw new ConflictAppException("PROCESS_DEFINITION_KEY_TAKEN", $"A process definition with key '{request.Key}' already exists.");
        }

        var definition = new ProcessDefinition
        {
            Key = request.Key,
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Status = ProcessDefinitionStatus.Draft,
        };
        _db.ProcessDefinitions.Add(definition);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.CreateProcess, nameof(ProcessDefinition), definition.Id.ToString(), newValue: new { definition.Key, definition.Name }, cancellationToken: cancellationToken);

        return new ProcessDefinitionDto(definition.Id, definition.Key, definition.Name, definition.Description, definition.Category, definition.Status, definition.CurrentVersionId);
    }

    public async Task<IReadOnlyList<ProcessVersionDto>> GetVersionsAsync(Guid processDefinitionId, CancellationToken cancellationToken = default)
    {
        var versions = await _db.ProcessVersions
            .AsNoTracking()
            .Where(v => v.ProcessDefinitionId == processDefinitionId)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync(cancellationToken);

        return versions.Select(ToDto).ToList();
    }

    public async Task<ProcessVersionDto> CreateVersionAsync(Guid processDefinitionId, CreateProcessVersionRequest request, CancellationToken cancellationToken = default)
    {
        await _createVersionValidator.ValidateAndThrowAsync(request, cancellationToken);

        var definitionExists = await _db.ProcessDefinitions.AnyAsync(p => p.Id == processDefinitionId, cancellationToken);
        if (!definitionExists)
        {
            throw new NotFoundAppException("PROCESS_DEFINITION_NOT_FOUND", $"Process definition '{processDefinitionId}' was not found.");
        }

        var hasDraft = await _db.ProcessVersions.AnyAsync(v => v.ProcessDefinitionId == processDefinitionId && v.Status == ProcessVersionStatus.Draft, cancellationToken);
        if (hasDraft)
        {
            throw new ConflictAppException("DRAFT_VERSION_ALREADY_EXISTS", "This process definition already has an unpublished draft version. Publish or discard it before creating another.");
        }

        var nextVersionNumber = 1 + await _db.ProcessVersions
            .Where(v => v.ProcessDefinitionId == processDefinitionId)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(cancellationToken) ?? 1;

        var version = new ProcessVersion
        {
            ProcessDefinitionId = processDefinitionId,
            VersionNumber = nextVersionNumber,
            DefinitionJson = WorkflowJson.Serialize(request.Definition),
            Status = ProcessVersionStatus.Draft,
        };
        _db.ProcessVersions.Add(version);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.ModifyProcess, nameof(ProcessVersion), version.Id.ToString(), newValue: new { processDefinitionId, version.VersionNumber }, cancellationToken: cancellationToken);

        return ToDto(version);
    }

    private static ProcessVersionDto ToDto(ProcessVersion version) =>
        new(version.Id, version.ProcessDefinitionId, version.VersionNumber, version.Status, WorkflowJson.TryDeserialize(version.DefinitionJson)!, version.PublishedAt);
}
