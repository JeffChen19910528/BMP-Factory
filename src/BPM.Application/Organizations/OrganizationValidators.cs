using FluentValidation;

namespace BPM.Application.Organizations;

// Phase 9 — OrganizationService.CreateAsync previously had no validator at all (a genuine
// pre-existing gap unrelated to this phase's own scope, but the same "no validation, only EF's
// column constraints" pattern Phase 5.5.2 already found and fixed for User/Department at the
// time). Closed here alongside the new Update validator, mirroring
// CreateDepartmentRequestValidator's own shape exactly.
public class CreateOrganizationRequestValidator : AbstractValidator<CreateOrganizationRequest>
{
    public CreateOrganizationRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(256);
    }
}

public class UpdateOrganizationRequestValidator : AbstractValidator<UpdateOrganizationRequest>
{
    public UpdateOrganizationRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(256);
        RuleFor(r => r.ExpectedVersion).NotEmpty();
    }
}
