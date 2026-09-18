using BPM.Domain.Workflow;
using FluentValidation;

namespace BPM.Application.Sla;

// Part T: "use existing identity semantics... do not allow an arbitrary email address." Only
// User/Role/DepartmentManager/ProcessInitiator are valid escalation targets — the same restriction
// AssignmentResolver.ResolveAsync already supports, minus Department (see EscalationPolicy.cs's
// own comment on why a whole department is excluded as an escalation target) and minus
// Manager/Dynamic (reserved/unimplemented everywhere else in this engine too).
internal static class EscalationTargetTypes
{
    public static readonly WorkflowAssignmentType[] Allowed =
    {
        WorkflowAssignmentType.User,
        WorkflowAssignmentType.Role,
        WorkflowAssignmentType.DepartmentManager,
        WorkflowAssignmentType.ProcessInitiator,
    };
}

public class CreateEscalationPolicyRequestValidator : AbstractValidator<CreateEscalationPolicyRequest>
{
    public CreateEscalationPolicyRequestValidator()
    {
        RuleFor(r => r.ProcessDefinitionId).NotEmpty();
        RuleFor(r => r.NodeId).NotEmpty().MaximumLength(128);
        RuleFor(r => r.DelayMinutes).GreaterThanOrEqualTo(0).WithMessage("Escalation delay must not be negative.");
        RuleFor(r => r.TargetType).Must(t => EscalationTargetTypes.Allowed.Contains(t))
            .WithMessage("Escalation target type must be User, Role, DepartmentManager, or ProcessInitiator.");
        RuleFor(r => r.TargetValue).NotEmpty()
            .Unless(r => r.TargetType == WorkflowAssignmentType.ProcessInitiator)
            .WithMessage("Target value is required unless the target type is ProcessInitiator.");
    }
}

public class UpdateEscalationPolicyRequestValidator : AbstractValidator<UpdateEscalationPolicyRequest>
{
    public UpdateEscalationPolicyRequestValidator()
    {
        RuleFor(r => r.DelayMinutes).GreaterThanOrEqualTo(0).WithMessage("Escalation delay must not be negative.");
        RuleFor(r => r.TargetType).Must(t => EscalationTargetTypes.Allowed.Contains(t))
            .WithMessage("Escalation target type must be User, Role, DepartmentManager, or ProcessInitiator.");
        RuleFor(r => r.TargetValue).NotEmpty()
            .Unless(r => r.TargetType == WorkflowAssignmentType.ProcessInitiator)
            .WithMessage("Target value is required unless the target type is ProcessInitiator.");
        RuleFor(r => r.ExpectedVersion).NotEmpty();
    }
}
