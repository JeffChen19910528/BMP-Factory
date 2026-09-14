using BPM.Application.Common;
using BPM.Application.Forms;
using BPM.Domain.Entities;
using BPM.Domain.Forms;
using BPM.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Plain CRUD only (create definition, create a draft version, list/get) — same split as
// ProcessDefinitionService: publishing (which needs FormSchemaValidator) is an engine operation
// and deliberately lives in BPM.Workflow.Engine.FormEngine instead.
public class FormDefinitionService : IFormDefinitionService
{
    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;
    private readonly IValidator<CreateFormDefinitionRequest> _createDefinitionValidator;
    private readonly IValidator<CreateFormVersionRequest> _createVersionValidator;

    public FormDefinitionService(
        BpmDbContext db,
        IAuditService auditService,
        IValidator<CreateFormDefinitionRequest> createDefinitionValidator,
        IValidator<CreateFormVersionRequest> createVersionValidator)
    {
        _db = db;
        _auditService = auditService;
        _createDefinitionValidator = createDefinitionValidator;
        _createVersionValidator = createVersionValidator;
    }

    public async Task<IReadOnlyList<FormDefinitionDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.FormDefinitions
            .AsNoTracking()
            .Select(f => new FormDefinitionDto(f.Id, f.Key, f.Name, f.Description, f.Category, f.Status, f.CurrentVersionId))
            .ToListAsync(cancellationToken);

    public async Task<FormDefinitionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _db.FormDefinitions
            .AsNoTracking()
            .Where(f => f.Id == id)
            .Select(f => new FormDefinitionDto(f.Id, f.Key, f.Name, f.Description, f.Category, f.Status, f.CurrentVersionId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<FormDefinitionDto> CreateAsync(CreateFormDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        await _createDefinitionValidator.ValidateAndThrowAsync(request, cancellationToken);

        var keyTaken = await _db.FormDefinitions.AnyAsync(f => f.Key == request.Key, cancellationToken);
        if (keyTaken)
        {
            throw new ConflictAppException("FORM_DEFINITION_KEY_TAKEN", $"A form definition with key '{request.Key}' already exists.");
        }

        var definition = new FormDefinition
        {
            Key = request.Key,
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            Status = FormDefinitionStatus.Draft,
        };
        _db.FormDefinitions.Add(definition);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.FormCreated, nameof(FormDefinition), definition.Id.ToString(), newValue: new { definition.Key, definition.Name }, cancellationToken: cancellationToken);

        return new FormDefinitionDto(definition.Id, definition.Key, definition.Name, definition.Description, definition.Category, definition.Status, definition.CurrentVersionId);
    }

    public async Task<IReadOnlyList<FormVersionDto>> GetVersionsAsync(Guid formDefinitionId, CancellationToken cancellationToken = default)
    {
        var versions = await _db.FormVersions
            .AsNoTracking()
            .Where(v => v.FormDefinitionId == formDefinitionId)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync(cancellationToken);

        return versions.Select(ToDto).ToList();
    }

    public async Task<FormVersionDto> CreateVersionAsync(Guid formDefinitionId, CreateFormVersionRequest request, CancellationToken cancellationToken = default)
    {
        await _createVersionValidator.ValidateAndThrowAsync(request, cancellationToken);

        var definitionExists = await _db.FormDefinitions.AnyAsync(f => f.Id == formDefinitionId, cancellationToken);
        if (!definitionExists)
        {
            throw new NotFoundAppException("FORM_DEFINITION_NOT_FOUND", $"Form definition '{formDefinitionId}' was not found.");
        }

        var hasDraft = await _db.FormVersions.AnyAsync(v => v.FormDefinitionId == formDefinitionId && v.Status == FormVersionStatus.Draft, cancellationToken);
        if (hasDraft)
        {
            throw new ConflictAppException("DRAFT_VERSION_ALREADY_EXISTS", "This form definition already has an unpublished draft version. Publish or discard it before creating another.");
        }

        var nextVersionNumber = 1 + await _db.FormVersions
            .Where(v => v.FormDefinitionId == formDefinitionId)
            .Select(v => (int?)v.VersionNumber)
            .MaxAsync(cancellationToken) ?? 1;

        var version = new FormVersion
        {
            FormDefinitionId = formDefinitionId,
            VersionNumber = nextVersionNumber,
            SchemaJson = FormJson.Serialize(request.Schema),
            Status = FormVersionStatus.Draft,
        };
        _db.FormVersions.Add(version);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.FormVersionCreated, nameof(FormVersion), version.Id.ToString(), newValue: new { formDefinitionId, version.VersionNumber }, cancellationToken: cancellationToken);

        return ToDto(version);
    }

    private static FormVersionDto ToDto(FormVersion version) =>
        new(version.Id, version.FormDefinitionId, version.VersionNumber, version.Status, FormJson.TryDeserialize(version.SchemaJson)!, version.PublishedAt);
}
