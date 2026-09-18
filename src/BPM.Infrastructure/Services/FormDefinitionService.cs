using BPM.Application.Common;
using BPM.Application.Forms;
using BPM.Domain.Entities;
using BPM.Domain.Forms;
using BPM.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Plain CRUD only (create definition, create/update a draft version, list/get) — same split as
// ProcessDefinitionService: publishing (which needs FormSchemaValidator) is an engine operation
// and deliberately lives in BPM.Workflow.Engine.FormEngine instead.
public class FormDefinitionService : IFormDefinitionService
{
    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly IValidator<CreateFormDefinitionRequest> _createDefinitionValidator;
    private readonly IValidator<CreateFormVersionRequest> _createVersionValidator;
    private readonly IValidator<UpdateFormDefinitionRequest> _updateDefinitionValidator;
    private readonly IValidator<UpdateFormVersionRequest> _updateVersionValidator;

    public FormDefinitionService(
        BpmDbContext db,
        IAuditService auditService,
        ICurrentUserService currentUser,
        IValidator<CreateFormDefinitionRequest> createDefinitionValidator,
        IValidator<CreateFormVersionRequest> createVersionValidator,
        IValidator<UpdateFormDefinitionRequest> updateDefinitionValidator,
        IValidator<UpdateFormVersionRequest> updateVersionValidator)
    {
        _db = db;
        _auditService = auditService;
        _currentUser = currentUser;
        _createDefinitionValidator = createDefinitionValidator;
        _createVersionValidator = createVersionValidator;
        _updateDefinitionValidator = updateDefinitionValidator;
        _updateVersionValidator = updateVersionValidator;
    }

    public async Task<IReadOnlyList<FormDefinitionDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _db.FormDefinitions.AsNoTracking().ToListAsync(cancellationToken);
        return entities.Select(ToDto).ToList();
    }

    public async Task<FormDefinitionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definition = await _db.FormDefinitions.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        return definition is null ? null : ToDto(definition);
    }

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
            CreatedBy = _currentUser.UserId,
        };
        _db.FormDefinitions.Add(definition);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.FormCreated, nameof(FormDefinition), definition.Id.ToString(), newValue: new { definition.Key, definition.Name }, cancellationToken: cancellationToken);

        return ToDto(definition);
    }

    public async Task<FormDefinitionDto> UpdateAsync(Guid id, UpdateFormDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        await _updateDefinitionValidator.ValidateAndThrowAsync(request, cancellationToken);

        var definition = await _db.FormDefinitions.SingleOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new NotFoundAppException("FORM_DEFINITION_NOT_FOUND", $"Form definition '{id}' was not found.");

        var oldValue = new { definition.Name, definition.Description, definition.Category };
        definition.Name = request.Name;
        definition.Description = request.Description;
        definition.Category = request.Category;
        definition.UpdatedAt = DateTime.UtcNow;
        definition.UpdatedBy = _currentUser.UserId;
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.FormModified, nameof(FormDefinition), definition.Id.ToString(), oldValue: oldValue, newValue: new { definition.Name, definition.Description, definition.Category }, cancellationToken: cancellationToken);

        return ToDto(definition);
    }

    public async Task<IReadOnlyList<FormVersionDto>> GetVersionsAsync(Guid formDefinitionId, CancellationToken cancellationToken = default)
    {
        var versions = await _db.FormVersions
            .AsNoTracking()
            .Where(v => v.FormDefinitionId == formDefinitionId)
            .OrderByDescending(v => v.VersionNumber)
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
            CreatedBy = _currentUser.UserId,
        };
        _db.FormVersions.Add(version);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.FormVersionCreated, nameof(FormVersion), version.Id.ToString(), newValue: new { formDefinitionId, version.VersionNumber }, cancellationToken: cancellationToken);

        return ToDto(version);
    }

    public async Task<FormVersionDto> UpdateVersionAsync(Guid formDefinitionId, Guid versionId, UpdateFormVersionRequest request, CancellationToken cancellationToken = default)
    {
        await _updateVersionValidator.ValidateAndThrowAsync(request, cancellationToken);

        var version = await _db.FormVersions
            .SingleOrDefaultAsync(v => v.Id == versionId && v.FormDefinitionId == formDefinitionId, cancellationToken)
            ?? throw new NotFoundAppException("FORM_VERSION_NOT_FOUND", $"Form version '{versionId}' was not found.");

        if (version.Status != FormVersionStatus.Draft)
        {
            throw new ConflictAppException("VERSION_NOT_DRAFT", "Only a Draft version can be edited — published versions are immutable.");
        }

        var oldValue = new { version.SchemaJson };
        version.SchemaJson = FormJson.Serialize(request.Schema);
        version.UpdatedAt = DateTime.UtcNow;
        version.UpdatedBy = _currentUser.UserId;

        // Optimistic concurrency (Skill.md §33) — identical pattern to
        // ProcessDefinitionService.UpdateVersionAsync (Phase 5.3.2) and FormEngine.SaveDataAsync:
        // pin the tracked entity's *original* RowVersion to what the client last read, so EF's
        // generated UPDATE ... WHERE RowVersion = @original affects zero rows (and throws
        // DbUpdateConcurrencyException) if someone else saved this draft in between.
        byte[] expectedVersion;
        try
        {
            expectedVersion = Convert.FromBase64String(request.ExpectedVersion);
        }
        catch (FormatException)
        {
            throw new BadRequestAppException("INVALID_EXPECTED_VERSION", "ExpectedVersion must be a base64-encoded RowVersion.");
        }
        _db.Entry(version).Property(v => v.RowVersion).OriginalValue = expectedVersion;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("FORM_VERSION_CONCURRENCY_CONFLICT", "This draft was modified by another request. Reload and retry.");
        }

        await _auditService.LogAsync(AuditActions.FormVersionUpdated, nameof(FormVersion), version.Id.ToString(), oldValue: oldValue, newValue: new { version.SchemaJson }, cancellationToken: cancellationToken);

        return ToDto(version);
    }

    private static FormDefinitionDto ToDto(FormDefinition definition) =>
        new(definition.Id, definition.Key, definition.Name, definition.Description, definition.Category, definition.Status, definition.CurrentVersionId, definition.CreatedAt, definition.CreatedBy, definition.UpdatedAt);

    private static FormVersionDto ToDto(FormVersion version) =>
        new(version.Id, version.FormDefinitionId, version.VersionNumber, version.Status, FormJson.TryDeserialize(version.SchemaJson)!, version.CreatedAt, version.CreatedBy, version.PublishedAt, version.PublishedBy, Convert.ToBase64String(version.RowVersion));
}
