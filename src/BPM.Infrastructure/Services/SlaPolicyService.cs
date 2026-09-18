using BPM.Application.Common;
using BPM.Application.Sla;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Phase 6.3 — minimal backend/API-level SLA policy configuration (Part R: no designer UI this
// phase). Follows the exact RowVersion/ExpectedVersion concurrency pattern already established for
// User/Department/ProcessVersion/FormVersion — see CLAUDE.md's own note on this recurring pattern.
public class SlaPolicyService : ISlaPolicyService
{
    private readonly BpmDbContext _db;
    private readonly IAuditService _auditService;
    private readonly IValidator<CreateSlaPolicyRequest> _createValidator;
    private readonly IValidator<UpdateSlaPolicyRequest> _updateValidator;

    public SlaPolicyService(BpmDbContext db, IAuditService auditService, IValidator<CreateSlaPolicyRequest> createValidator, IValidator<UpdateSlaPolicyRequest> updateValidator)
    {
        _db = db;
        _auditService = auditService;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<IReadOnlyList<SlaPolicyDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.SlaPolicies.AsNoTracking().Select(p => ToDto(p)).ToListAsync(cancellationToken);

    public async Task<SlaPolicyDto> CreateAsync(CreateSlaPolicyRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var processExists = await _db.ProcessDefinitions.AnyAsync(p => p.Id == request.ProcessDefinitionId, cancellationToken);
        if (!processExists)
        {
            throw new BadRequestAppException("INVALID_PROCESS_DEFINITION", $"Process definition '{request.ProcessDefinitionId}' was not found.");
        }

        // Part I: "duplicate conflicting policy where applicable" — one policy per (process, node),
        // matching the unique index (SlaPolicyConfiguration). Editing an existing step's policy is
        // an Update, never a second Create for the same pair.
        var exists = await _db.SlaPolicies.AnyAsync(p => p.ProcessDefinitionId == request.ProcessDefinitionId && p.NodeId == request.NodeId, cancellationToken);
        if (exists)
        {
            throw new ConflictAppException("SLA_POLICY_ALREADY_EXISTS", $"An SLA policy for node '{request.NodeId}' on this process already exists. Use update instead.");
        }

        var policy = new SlaPolicy
        {
            ProcessDefinitionId = request.ProcessDefinitionId,
            NodeId = request.NodeId,
            Enabled = request.Enabled,
            DurationMinutes = request.DurationMinutes,
            WarningOffsetMinutes = request.WarningOffsetMinutes,
        };
        _db.SlaPolicies.Add(policy);
        await _db.SaveChangesAsync(cancellationToken);

        // Phase 9 Part 5 — this service had zero audit coverage before; added following
        // DepartmentService.CreateAsync's exact convention (a plain string action name, matching
        // the sibling Create* calls elsewhere in this codebase, not every one of which uses an
        // AuditActions constant).
        await _auditService.LogAsync(AuditActions.CreateSlaPolicy, nameof(SlaPolicy), policy.Id.ToString(), newValue: new { policy.ProcessDefinitionId, policy.NodeId, policy.Enabled, policy.DurationMinutes, policy.WarningOffsetMinutes }, cancellationToken: cancellationToken);

        return ToDto(policy);
    }

    public async Task<SlaPolicyDto?> UpdateAsync(Guid id, UpdateSlaPolicyRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var policy = await _db.SlaPolicies.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null)
        {
            return null;
        }

        // Part J/X: editing here only ever affects *future* TaskInstances resolved by SlaEngine —
        // every already-created TaskSla keeps its own persisted StartedAt/WarningAt/DueAt
        // regardless of what happens to this row afterward. Nothing here touches the TaskSlas
        // table at all.
        var oldValue = new { policy.Enabled, policy.DurationMinutes, policy.WarningOffsetMinutes };

        policy.Enabled = request.Enabled;
        policy.DurationMinutes = request.DurationMinutes;
        policy.WarningOffsetMinutes = request.WarningOffsetMinutes;

        byte[] expectedVersion;
        try
        {
            expectedVersion = Convert.FromBase64String(request.ExpectedVersion);
        }
        catch (FormatException)
        {
            throw new BadRequestAppException("INVALID_EXPECTED_VERSION", "ExpectedVersion must be a base64-encoded RowVersion.");
        }
        _db.Entry(policy).Property(p => p.RowVersion).OriginalValue = expectedVersion;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("SLA_POLICY_CONCURRENCY_CONFLICT", "This SLA policy was modified by another administrator. Reload and retry.");
        }

        await _auditService.LogAsync(AuditActions.UpdateSlaPolicy, nameof(SlaPolicy), policy.Id.ToString(), oldValue, new { policy.Enabled, policy.DurationMinutes, policy.WarningOffsetMinutes }, cancellationToken);

        return ToDto(policy);
    }

    private static SlaPolicyDto ToDto(SlaPolicy p) =>
        new(p.Id, p.ProcessDefinitionId, p.NodeId, p.Enabled, p.DurationMinutes, p.WarningOffsetMinutes, Convert.ToBase64String(p.RowVersion));
}
