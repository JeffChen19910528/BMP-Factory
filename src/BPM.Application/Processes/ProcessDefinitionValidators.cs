using FluentValidation;

namespace BPM.Application.Processes;

public class CreateProcessDefinitionRequestValidator : AbstractValidator<CreateProcessDefinitionRequest>
{
    public CreateProcessDefinitionRequestValidator()
    {
        RuleFor(r => r.Key)
            .NotEmpty()
            .MaximumLength(200)
            .Matches("^[a-zA-Z0-9_-]+$")
            .WithMessage("Key must contain only letters, digits, '-' and '_'.");

        RuleFor(r => r.Name).NotEmpty().MaximumLength(256);
        RuleFor(r => r.Category).MaximumLength(128);
    }
}

public class UpdateProcessDefinitionRequestValidator : AbstractValidator<UpdateProcessDefinitionRequest>
{
    public UpdateProcessDefinitionRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(256);
        RuleFor(r => r.Category).MaximumLength(128);
    }
}

public class CreateProcessVersionRequestValidator : AbstractValidator<CreateProcessVersionRequest>
{
    public CreateProcessVersionRequestValidator()
    {
        RuleFor(r => r.Definition).NotNull();
        RuleFor(r => r.Definition.Nodes).NotEmpty().When(r => r.Definition is not null)
            .WithMessage("Definition must contain at least one node.");
    }
}

public class UpdateProcessVersionRequestValidator : AbstractValidator<UpdateProcessVersionRequest>
{
    public UpdateProcessVersionRequestValidator()
    {
        RuleFor(r => r.Definition).NotNull();
        RuleFor(r => r.Definition.Nodes).NotEmpty().When(r => r.Definition is not null)
            .WithMessage("Definition must contain at least one node.");
        RuleFor(r => r.ExpectedVersion).NotEmpty()
            .WithMessage("ExpectedVersion is required — pass the RowVersion last read from this ProcessVersion.");
    }
}
