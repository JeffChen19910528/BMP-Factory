using FluentValidation;

namespace BPM.Application.Forms;

public class CreateFormDefinitionRequestValidator : AbstractValidator<CreateFormDefinitionRequest>
{
    public CreateFormDefinitionRequestValidator()
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

public class UpdateFormDefinitionRequestValidator : AbstractValidator<UpdateFormDefinitionRequest>
{
    public UpdateFormDefinitionRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(256);
        RuleFor(r => r.Category).MaximumLength(128);
    }
}

public class CreateFormVersionRequestValidator : AbstractValidator<CreateFormVersionRequest>
{
    public CreateFormVersionRequestValidator()
    {
        RuleFor(r => r.Schema).NotNull();
        RuleFor(r => r.Schema.Fields).NotEmpty().When(r => r.Schema is not null)
            .WithMessage("Schema must contain at least one field.");
    }
}

public class UpdateFormVersionRequestValidator : AbstractValidator<UpdateFormVersionRequest>
{
    public UpdateFormVersionRequestValidator()
    {
        RuleFor(r => r.Schema).NotNull();
        RuleFor(r => r.Schema.Fields).NotEmpty().When(r => r.Schema is not null)
            .WithMessage("Schema must contain at least one field.");
        RuleFor(r => r.ExpectedVersion).NotEmpty()
            .WithMessage("ExpectedVersion is required — pass the RowVersion last read from this FormVersion.");
    }
}
