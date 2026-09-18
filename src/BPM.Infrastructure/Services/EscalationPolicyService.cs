using BPM.Application.Common;
using BPM.Application.Sla;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

// Phase 6.4 — minimal escalation policy configuration, mirroring SlaPolicyService's own shape
// exactly (same RowVersion/ExpectedVersion concurrency pattern, same Administrator-only
// controller-level gate — see EscalationPoliciesController).
public class EscalationPolicyService : IEscalationPolicyService
{
    private readonly BpmDbContext _db;
    private readonly IValidator<CreateEscalationPolicyRequest> _createValidator;
    private readonly IValidator<UpdateEscalationPolicyRequest> _updateValidator;

    public EscalationPolicyService(BpmDbContext db, IValidator<CreateEscalationPolicyRequest> createValidator, IValidator<UpdateEscalationPolicyRequest> updateValidator)
    {
        _db = db;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<IReadOnlyList<EscalationPolicyDto>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _db.EscalationPolicies.AsNoTracking().Select(p => ToDto(p)).ToListAsync(cancellationToken);

    public async Task<EscalationPolicyDto> CreateAsync(CreateEscalationPolicyRequest request, CancellationToken cancellationToken = default)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);

        var processExists = await _db.ProcessDefinitions.AnyAsync(p => p.Id == request.ProcessDefinitionId, cancellationToken);
        if (!processExists)
        {
            throw new BadRequestAppException("INVALID_PROCESS_DEFINITION", $"Process definition '{request.ProcessDefinitionId}' was not found.");
        }

        var exists = await _db.EscalationPolicies.AnyAsync(p => p.ProcessDefinitionId == request.ProcessDefinitionId && p.NodeId == request.NodeId, cancellationToken);
        if (exists)
        {
            throw new ConflictAppException("ESCALATION_POLICY_ALREADY_EXISTS", $"An escalation policy for node '{request.NodeId}' on this process already exists. Use update instead.");
        }

        var policy = new EscalationPolicy
        {
            ProcessDefinitionId = request.ProcessDefinitionId,
            NodeId = request.NodeId,
            Enabled = request.Enabled,
            DelayMinutes = request.DelayMinutes,
            TargetType = request.TargetType,
            TargetValue = request.TargetValue,
        };
        _db.EscalationPolicies.Add(policy);
        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(policy);
    }

    public async Task<EscalationPolicyDto?> UpdateAsync(Guid id, UpdateEscalationPolicyRequest request, CancellationToken cancellationToken = default)
    {
        await _updateValidator.ValidateAndThrowAsync(request, cancellationToken);

        var policy = await _db.EscalationPolicies.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (policy is null)
        {
            return null;
        }

        // Same Part J/X guarantee as SlaPolicy: only affects TaskSla rows that become Overdue
        // *after* this change — an already-computed TaskSla.EscalationAt is never recalculated.
        policy.Enabled = request.Enabled;
        policy.DelayMinutes = request.DelayMinutes;
        policy.TargetType = request.TargetType;
        policy.TargetValue = request.TargetValue;

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
            throw new ConflictAppException("ESCALATION_POLICY_CONCURRENCY_CONFLICT", "This escalation policy was modified by another administrator. Reload and retry.");
        }

        return ToDto(policy);
    }

    private static EscalationPolicyDto ToDto(EscalationPolicy p) =>
        new(p.Id, p.ProcessDefinitionId, p.NodeId, p.Enabled, p.DelayMinutes, p.TargetType, p.TargetValue, Convert.ToBase64String(p.RowVersion));
}
