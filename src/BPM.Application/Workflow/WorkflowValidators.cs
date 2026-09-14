using FluentValidation;

namespace BPM.Application.Workflow;

public class StartProcessRequestValidator : AbstractValidator<StartProcessRequest>
{
    public StartProcessRequestValidator()
    {
        RuleFor(r => r.ProcessDefinitionKey).NotEmpty().MaximumLength(200);
        RuleFor(r => r.BusinessKey).MaximumLength(256);
    }
}
