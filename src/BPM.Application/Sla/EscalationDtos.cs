using BPM.Domain.Workflow;

namespace BPM.Application.Sla;

public record EscalationPolicyDto(
    Guid Id,
    Guid ProcessDefinitionId,
    string NodeId,
    bool Enabled,
    int DelayMinutes,
    WorkflowAssignmentType TargetType,
    string TargetValue,
    string RowVersion);

public record CreateEscalationPolicyRequest(Guid ProcessDefinitionId, string NodeId, bool Enabled, int DelayMinutes, WorkflowAssignmentType TargetType, string TargetValue);

public record UpdateEscalationPolicyRequest(bool Enabled, int DelayMinutes, WorkflowAssignmentType TargetType, string TargetValue, string ExpectedVersion);

// Phase 6.4 — minimal escalation policy configuration, Administrator-only (Part AC), same
// RowVersion/ExpectedVersion concurrency shape as ISlaPolicyService.
public interface IEscalationPolicyService
{
    Task<IReadOnlyList<EscalationPolicyDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<EscalationPolicyDto> CreateAsync(CreateEscalationPolicyRequest request, CancellationToken cancellationToken = default);
    Task<EscalationPolicyDto?> UpdateAsync(Guid id, UpdateEscalationPolicyRequest request, CancellationToken cancellationToken = default);
}
