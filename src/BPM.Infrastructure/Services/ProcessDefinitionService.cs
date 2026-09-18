using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Plain CRUD (create definition, create/update a draft version, list/get) plus, as of Phase 8,
// Process Governance & Lifecycle (Suspend/Archive/Restore/Owner/Version-Compare). Publishing and
// running a process remain engine operations — see BPM.Workflow.Engine.WorkflowEngine — this
// service still deliberately doesn't depend on the workflow validator/engine.
//
// Phase 8 authorization model (no new subsystem): every mutating method here remains either
// Administrator-only (Create/Update/CreateVersion/UpdateVersion/AssignOwner — enforced by the
// controller's own [Authorize(Roles="Administrator")], unchanged) or, for the three lifecycle
// transitions only, Administrator-OR-the-definition's-own-OwnerUserId (computed here, from the
// loaded entity, never trusted from the request — see EnsureGovernanceAuthorized). This reuses
// the existing Role/RowVersion/AuditLog architecture verbatim; no ProcessPermission table, no new
// authorization framework.
//
// Audit atomicity note: like every other mutating method already in this file (Create/Update/
// CreateVersion/UpdateVersion), the new governance methods call _auditService.LogAsync *after* a
// successful SaveChangesAsync — two separate round-trips, not one spanning transaction. This is
// this file's own pre-existing, unchanged convention (IAuditService.LogAsync does its own
// SaveChangesAsync) — WorkflowEngine's single-SaveChangesAsync-covers-everything pattern lives in
// a different assembly (BPM.Workflow) that this service does not and should not depend on.
public class ProcessDefinitionService : IProcessDefinitionService
{
    private const string AdministratorRole = "Administrator";

    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;
    private readonly ICurrentUserService _currentUser;
    private readonly IValidator<CreateProcessDefinitionRequest> _createDefinitionValidator;
    private readonly IValidator<CreateProcessVersionRequest> _createVersionValidator;
    private readonly IValidator<UpdateProcessVersionRequest> _updateVersionValidator;
    private readonly IValidator<UpdateProcessDefinitionRequest> _updateDefinitionValidator;

    public ProcessDefinitionService(
        BpmDbContext db,
        IAuditService auditService,
        ICurrentUserService currentUser,
        IValidator<CreateProcessDefinitionRequest> createDefinitionValidator,
        IValidator<CreateProcessVersionRequest> createVersionValidator,
        IValidator<UpdateProcessVersionRequest> updateVersionValidator,
        IValidator<UpdateProcessDefinitionRequest> updateDefinitionValidator)
    {
        _db = db;
        _auditService = auditService;
        _currentUser = currentUser;
        _createDefinitionValidator = createDefinitionValidator;
        _createVersionValidator = createVersionValidator;
        _updateVersionValidator = updateVersionValidator;
        _updateDefinitionValidator = updateDefinitionValidator;
    }

    public async Task<PagedResult<ProcessDefinitionDto>> GetAllAsync(ProcessDefinitionQuery query, CancellationToken cancellationToken = default)
    {
        var q = _db.ProcessDefinitions.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            q = q.Where(p => EF.Functions.ILike(p.Key, $"%{search}%") || EF.Functions.ILike(p.Name, $"%{search}%"));
        }
        if (query.Status is not null)
        {
            q = q.Where(p => p.Status == query.Status);
        }

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 200 ? 20 : query.PageSize;

        // Phase 12 — same overflow guard Phase 7.2.3 established in ProcessMonitoringQueryService
        // (see that file's own comment): long arithmetic clamped to int.MaxValue prevents an
        // extreme Page value from wrapping to a negative Skip() argument.
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

        var totalCount = await q.CountAsync(cancellationToken);
        var entities = await q
            .OrderByDescending(p => p.CreatedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<ProcessDefinitionDto>(entities.Select(ToDto).ToList(), totalCount, page, pageSize);
    }

    public async Task<ProcessDefinitionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var definition = await _db.ProcessDefinitions.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return definition is null ? null : ToDto(definition);
    }

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
            CreatedBy = _currentUser.UserId,
        };
        _db.ProcessDefinitions.Add(definition);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.CreateProcess, nameof(ProcessDefinition), definition.Id.ToString(), newValue: new { definition.Key, definition.Name }, cancellationToken: cancellationToken);

        return ToDto(definition);
    }

    public async Task<ProcessDefinitionDto> UpdateAsync(Guid id, UpdateProcessDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        await _updateDefinitionValidator.ValidateAndThrowAsync(request, cancellationToken);

        var definition = await _db.ProcessDefinitions.SingleOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundAppException("PROCESS_DEFINITION_NOT_FOUND", $"Process definition '{id}' was not found.");

        // Phase 8 Part 12 — closes a pre-existing governance gap: metadata edits previously had no
        // status gate at all, so an Administrator could rename/re-describe/re-categorize a process
        // regardless of lifecycle state, contradicting the frontend's "Archived is frozen" framing.
        // Draft and Published remain freely editable (unchanged behavior); Suspended/Archived now
        // require Restore first — the smallest rule that actually closes the inconsistency without
        // reopening a Published ProcessVersion or inventing a second edit-permission concept.
        EnsureMetadataEditable(definition);

        var oldValue = new { definition.Name, definition.Description, definition.Category };
        definition.Name = request.Name;
        definition.Description = request.Description;
        definition.Category = request.Category;
        definition.UpdatedAt = DateTime.UtcNow;
        definition.UpdatedBy = _currentUser.UserId;
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.ModifyProcess, nameof(ProcessDefinition), definition.Id.ToString(), oldValue: oldValue, newValue: new { definition.Name, definition.Description, definition.Category }, cancellationToken: cancellationToken);

        return ToDto(definition);
    }

    public async Task<IReadOnlyList<ProcessVersionDto>> GetVersionsAsync(Guid processDefinitionId, CancellationToken cancellationToken = default)
    {
        var versions = await _db.ProcessVersions
            .AsNoTracking()
            .Where(v => v.ProcessDefinitionId == processDefinitionId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync(cancellationToken);

        return versions.Select(ToDto).ToList();
    }

    public async Task<ProcessVersionDto> CreateVersionAsync(Guid processDefinitionId, CreateProcessVersionRequest request, CancellationToken cancellationToken = default)
    {
        await _createVersionValidator.ValidateAndThrowAsync(request, cancellationToken);

        var definition = await _db.ProcessDefinitions.AsNoTracking().SingleOrDefaultAsync(p => p.Id == processDefinitionId, cancellationToken)
            ?? throw new NotFoundAppException("PROCESS_DEFINITION_NOT_FOUND", $"Process definition '{processDefinitionId}' was not found.");

        // Phase 8 — same governed-lifecycle-state gate as metadata edits (Part 9's own decision
        // extended consistently here): a new Draft cannot be started while Suspended/Archived
        // without Restoring first. Draft (the very first version) and Published (creating the
        // next version) remain allowed, unchanged from pre-Phase-8 behavior.
        EnsureMetadataEditable(definition);

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
            CreatedBy = _currentUser.UserId,
        };
        _db.ProcessVersions.Add(version);
        await _db.SaveChangesAsync(cancellationToken);

        await _auditService.LogAsync(AuditActions.ModifyProcess, nameof(ProcessVersion), version.Id.ToString(), newValue: new { processDefinitionId, version.VersionNumber }, cancellationToken: cancellationToken);

        return ToDto(version);
    }

    public async Task<ProcessVersionDto> UpdateVersionAsync(Guid processDefinitionId, Guid versionId, UpdateProcessVersionRequest request, CancellationToken cancellationToken = default)
    {
        await _updateVersionValidator.ValidateAndThrowAsync(request, cancellationToken);

        var version = await _db.ProcessVersions
            .SingleOrDefaultAsync(v => v.Id == versionId && v.ProcessDefinitionId == processDefinitionId, cancellationToken)
            ?? throw new NotFoundAppException("PROCESS_VERSION_NOT_FOUND", $"Process version '{versionId}' was not found.");

        if (version.Status != ProcessVersionStatus.Draft)
        {
            throw new ConflictAppException("VERSION_NOT_DRAFT", "Only a Draft version can be edited — published versions are immutable.");
        }

        var oldValue = new { version.DefinitionJson };
        version.DefinitionJson = WorkflowJson.Serialize(request.Definition);
        version.UpdatedAt = DateTime.UtcNow;
        version.UpdatedBy = _currentUser.UserId;

        // Optimistic concurrency (Skill.md §33) — same pattern as FormEngine.SaveDataAsync: pin
        // the tracked entity's *original* RowVersion to what the client last read, so EF's
        // generated UPDATE ... WHERE RowVersion = @original naturally affects zero rows (and
        // throws DbUpdateConcurrencyException) if someone else saved this draft in between. Before
        // this fix, UpdateVersionAsync had no concurrency check at all — a real pre-existing gap,
        // not a Phase 5.3.2 design choice, found while wiring the Designer's Save Draft flow.
        _db.Entry(version).Property(v => v.RowVersion).OriginalValue = ParseExpectedVersion(request.ExpectedVersion);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("PROCESS_VERSION_CONCURRENCY_CONFLICT", "This draft was modified by another request. Reload and retry.");
        }

        await _auditService.LogAsync(AuditActions.ModifyProcess, nameof(ProcessVersion), version.Id.ToString(), oldValue: oldValue, newValue: new { version.DefinitionJson }, cancellationToken: cancellationToken);

        return ToDto(version);
    }

    // ---- Phase 8 — Process Governance & Lifecycle ----

    public async Task<ProcessDefinitionDto> SuspendAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ProcessLifecycleActionRequest request, CancellationToken cancellationToken = default)
    {
        var definition = await LoadTrackedAsync(id, cancellationToken);
        EnsureGovernanceAuthorized(definition, currentUserId, currentUserRoles);

        // Lifecycle state machine (Part 11): Suspend is only valid from Published. Draft has never
        // been live, and Archived must go through Restore (Archived -> Published) first, never
        // straight to Suspended — one canonical path back to "governed and live," not two.
        if (definition.Status != ProcessDefinitionStatus.Published)
        {
            throw new ConflictAppException("INVALID_LIFECYCLE_TRANSITION", $"Only a Published process definition can be suspended (current status: {definition.Status}).");
        }

        return await ApplyTransitionAsync(definition, ProcessDefinitionStatus.Suspended, AuditActions.SuspendProcess, currentUserId, request.ExpectedVersion, cancellationToken);
    }

    public async Task<ProcessDefinitionDto> ArchiveAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ProcessLifecycleActionRequest request, CancellationToken cancellationToken = default)
    {
        var definition = await LoadTrackedAsync(id, cancellationToken);
        EnsureGovernanceAuthorized(definition, currentUserId, currentUserRoles);

        // Both Published -> Archived and Suspended -> Archived are allowed (Part 9: "decide
        // whether Published -> Archived is allowed... prefer explicit rules" — both are accepted
        // here since nothing in this system's real usage demonstrates a need to force a Suspend
        // step before Archiving; Draft is rejected, since a never-published definition has no
        // running instances to preserve and "archiving" it has no defined meaning here).
        if (definition.Status is not (ProcessDefinitionStatus.Published or ProcessDefinitionStatus.Suspended))
        {
            throw new ConflictAppException("INVALID_LIFECYCLE_TRANSITION", $"Only a Published or Suspended process definition can be archived (current status: {definition.Status}).");
        }

        return await ApplyTransitionAsync(definition, ProcessDefinitionStatus.Archived, AuditActions.ArchiveProcess, currentUserId, request.ExpectedVersion, cancellationToken);
    }

    public async Task<ProcessDefinitionDto> RestoreAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ProcessLifecycleActionRequest request, CancellationToken cancellationToken = default)
    {
        var definition = await LoadTrackedAsync(id, cancellationToken);
        EnsureGovernanceAuthorized(definition, currentUserId, currentUserRoles);

        if (definition.Status is not (ProcessDefinitionStatus.Suspended or ProcessDefinitionStatus.Archived))
        {
            throw new ConflictAppException("INVALID_LIFECYCLE_TRANSITION", $"Only a Suspended or Archived process definition can be restored (current status: {definition.Status}).");
        }

        // Part 10 — Restore never touches ProcessVersion; it must only ever flip the definition's
        // own Status back to Published, relying on the CurrentVersionId already pinned from the
        // original publish. Defensively re-verified here (rather than assumed) — if this
        // definition's CurrentVersionId is somehow missing or no longer points at a real Published
        // version, fail closed instead of silently leaving it startable with no valid version.
        if (definition.CurrentVersionId is null)
        {
            throw new ConflictAppException("PROCESS_DEFINITION_NO_PUBLISHED_VERSION", "This process definition has no published version to restore to.");
        }
        var currentVersionStillValid = await _db.ProcessVersions.AsNoTracking()
            .AnyAsync(v => v.Id == definition.CurrentVersionId && v.Status == ProcessVersionStatus.Published, cancellationToken);
        if (!currentVersionStillValid)
        {
            throw new ConflictAppException("PROCESS_DEFINITION_NO_PUBLISHED_VERSION", "This process definition's current version is no longer a valid published version.");
        }

        return await ApplyTransitionAsync(definition, ProcessDefinitionStatus.Published, AuditActions.RestoreProcess, currentUserId, request.ExpectedVersion, cancellationToken);
    }

    public async Task<ProcessDefinitionDto> AssignOwnerAsync(Guid id, AssignProcessOwnerRequest request, CancellationToken cancellationToken = default)
    {
        // Administrator-only — enforced by the controller's [Authorize(Roles="Administrator")],
        // matching Create/Update/Publish's existing gating; ownership assignment is deliberately
        // not something an existing owner can do to themselves or hand off unsupervised (Part 3).
        var definition = await LoadTrackedAsync(id, cancellationToken);

        if (request.OwnerUserId is Guid ownerId)
        {
            await EnsureOwnerIsValidAsync(ownerId, cancellationToken);
        }

        _db.Entry(definition).Property(p => p.RowVersion).OriginalValue = ParseExpectedVersion(request.ExpectedVersion);

        var oldOwnerId = definition.OwnerUserId;
        definition.OwnerUserId = request.OwnerUserId;
        definition.UpdatedAt = DateTime.UtcNow;
        definition.UpdatedBy = _currentUser.UserId;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("PROCESS_DEFINITION_CONCURRENCY_CONFLICT", "This process definition was modified by another request. Reload and retry.");
        }

        await _auditService.LogAsync(AuditActions.ChangeProcessOwner, nameof(ProcessDefinition), definition.Id.ToString(), oldValue: new { OwnerUserId = oldOwnerId }, newValue: new { OwnerUserId = request.OwnerUserId }, cancellationToken: cancellationToken);

        return ToDto(definition);
    }

    public async Task<VersionComparisonResponse> CompareVersionsAsync(Guid processDefinitionId, Guid fromVersionId, Guid toVersionId, CancellationToken cancellationToken = default)
    {
        // Part 16 — both versions must belong to the ProcessDefinition named in the route; the
        // exact same "Id + ProcessDefinitionId" guard UpdateVersionAsync already uses, reused
        // rather than reinvented. A version that exists but belongs to a different definition is
        // reported identically to "doesn't exist" (404), never distinguished — that distinction
        // itself would leak that the id is valid for *some* process, which is exactly the kind of
        // cross-definition information disclosure this guard exists to prevent.
        var fromVersion = await _db.ProcessVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == fromVersionId && v.ProcessDefinitionId == processDefinitionId, cancellationToken)
            ?? throw new NotFoundAppException("PROCESS_VERSION_NOT_FOUND", $"Process version '{fromVersionId}' was not found.");
        var toVersion = await _db.ProcessVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == toVersionId && v.ProcessDefinitionId == processDefinitionId, cancellationToken)
            ?? throw new NotFoundAppException("PROCESS_VERSION_NOT_FOUND", $"Process version '{toVersionId}' was not found.");

        var fromDefinition = WorkflowJson.TryDeserialize(fromVersion.DefinitionJson)
            ?? throw new ConflictAppException("PROCESS_VERSION_CORRUPT", "The 'from' process version's definition could not be parsed.");
        var toDefinition = WorkflowJson.TryDeserialize(toVersion.DefinitionJson)
            ?? throw new ConflictAppException("PROCESS_VERSION_CORRUPT", "The 'to' process version's definition could not be parsed.");

        return VersionComparer.Compare(processDefinitionId, fromVersion.Id, fromVersion.VersionNumber, toVersion.Id, toVersion.VersionNumber, fromDefinition, toDefinition);
    }

    // ---- Phase 8 governance helpers ----

    private async Task<ProcessDefinition> LoadTrackedAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.ProcessDefinitions.SingleOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundAppException("PROCESS_DEFINITION_NOT_FOUND", $"Process definition '{id}' was not found.");

    // Administrator OR the definition's own OwnerUserId (never a client-supplied identity —
    // always compared against the entity just loaded from the database and the authenticated
    // caller's own JWT-derived identity). If no owner is assigned, only an Administrator may act
    // (Part 3's own explicit default).
    private static void EnsureGovernanceAuthorized(ProcessDefinition definition, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles)
    {
        if (currentUserRoles.Contains(AdministratorRole))
        {
            return;
        }
        if (definition.OwnerUserId is Guid ownerId && ownerId == currentUserId)
        {
            return;
        }
        throw new ForbiddenAppException("PROCESS_DEFINITION_NOT_AUTHORIZED", "You are not authorized to perform this governance action on this process definition.");
    }

    // Part 12 — Draft and Published are freely editable (unchanged); Suspended/Archived require
    // Restore first. Shared by metadata edits and new-draft-version creation.
    private static void EnsureMetadataEditable(ProcessDefinition definition)
    {
        if (definition.Status is ProcessDefinitionStatus.Suspended or ProcessDefinitionStatus.Archived)
        {
            throw new ConflictAppException("PROCESS_DEFINITION_NOT_EDITABLE", $"This process definition is {definition.Status} — restore it to Published before editing it.");
        }
    }

    // Mirrors DepartmentService.EnsureManagerIsValidAsync's own existing pattern exactly (Phase
    // 5.5.2) — the same "exists and is active" check already applied to Department.ManagerUserId,
    // reused rather than inventing a new active/inactive rule for Owner (Part 4).
    private async Task EnsureOwnerIsValidAsync(Guid ownerId, CancellationToken cancellationToken)
    {
        var owner = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == ownerId, cancellationToken);
        if (owner is null)
        {
            throw new BadRequestAppException("INVALID_OWNER", $"Owner user '{ownerId}' was not found.");
        }
        if (!owner.IsActive)
        {
            throw new BadRequestAppException("INVALID_OWNER", "Owner user is not active.");
        }
    }

    private static byte[] ParseExpectedVersion(string expectedVersion)
    {
        try
        {
            return Convert.FromBase64String(expectedVersion);
        }
        catch (FormatException)
        {
            throw new BadRequestAppException("INVALID_EXPECTED_VERSION", "ExpectedVersion must be a base64-encoded RowVersion.");
        }
    }

    private async Task<ProcessDefinitionDto> ApplyTransitionAsync(ProcessDefinition definition, ProcessDefinitionStatus newStatus, string auditAction, Guid currentUserId, string expectedVersion, CancellationToken cancellationToken)
    {
        _db.Entry(definition).Property(p => p.RowVersion).OriginalValue = ParseExpectedVersion(expectedVersion);

        var oldStatus = definition.Status;
        definition.Status = newStatus;
        definition.UpdatedAt = DateTime.UtcNow;
        definition.UpdatedBy = currentUserId;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("PROCESS_DEFINITION_CONCURRENCY_CONFLICT", "This process definition was modified by another request. Reload and retry.");
        }

        await _auditService.LogAsync(auditAction, nameof(ProcessDefinition), definition.Id.ToString(), oldValue: new { Status = oldStatus }, newValue: new { Status = newStatus }, cancellationToken: cancellationToken);

        return ToDto(definition);
    }

    private static ProcessDefinitionDto ToDto(ProcessDefinition definition) =>
        new(definition.Id, definition.Key, definition.Name, definition.Description, definition.Category, definition.Status, definition.CurrentVersionId, definition.CreatedAt, definition.CreatedBy, definition.UpdatedAt, definition.OwnerUserId, Convert.ToBase64String(definition.RowVersion));

    private static ProcessVersionDto ToDto(ProcessVersion version) =>
        new(version.Id, version.ProcessDefinitionId, version.VersionNumber, version.Status, WorkflowJson.TryDeserialize(version.DefinitionJson)!, version.CreatedAt, version.CreatedBy, version.PublishedAt, version.PublishedBy, version.ChangeReason, Convert.ToBase64String(version.RowVersion));
}
