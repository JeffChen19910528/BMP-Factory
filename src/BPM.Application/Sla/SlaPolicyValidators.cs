using FluentValidation;

namespace BPM.Application.Sla;

// Part I: duration/warning-offset validation lives here (checked before the request ever reaches
// the calculator) as well as inside SlaCalculator itself (checked again for every task creation,
// defensively, in case a legacy/corrupted row ever bypasses this) — see SlaCalculator's own
// comment on why both checks exist rather than trusting just one.
public class CreateSlaPolicyRequestValidator : AbstractValidator<CreateSlaPolicyRequest>
{
    public CreateSlaPolicyRequestValidator()
    {
        RuleFor(r => r.ProcessDefinitionId).NotEmpty();
        RuleFor(r => r.NodeId).NotEmpty().MaximumLength(128);
        RuleFor(r => r.DurationMinutes).GreaterThan(0).WithMessage("Duration must be a positive number of minutes.");
        RuleFor(r => r.WarningOffsetMinutes).GreaterThanOrEqualTo(0).WithMessage("Warning offset must not be negative.");
        RuleFor(r => r).Must(r => r.WarningOffsetMinutes < r.DurationMinutes)
            .WithMessage("Warning offset must be less than the duration.")
            .WithName(nameof(CreateSlaPolicyRequest.WarningOffsetMinutes));
    }
}

public class UpdateSlaPolicyRequestValidator : AbstractValidator<UpdateSlaPolicyRequest>
{
    public UpdateSlaPolicyRequestValidator()
    {
        RuleFor(r => r.DurationMinutes).GreaterThan(0).WithMessage("Duration must be a positive number of minutes.");
        RuleFor(r => r.WarningOffsetMinutes).GreaterThanOrEqualTo(0).WithMessage("Warning offset must not be negative.");
        RuleFor(r => r).Must(r => r.WarningOffsetMinutes < r.DurationMinutes)
            .WithMessage("Warning offset must be less than the duration.")
            .WithName(nameof(UpdateSlaPolicyRequest.WarningOffsetMinutes));
        RuleFor(r => r.ExpectedVersion).NotEmpty();
    }
}
