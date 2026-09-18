using FluentValidation;

namespace BPM.Application.Users;

// Phase 5.5.2: no validator existed for either request before this phase (Phase 1 shipped Create/
// Update with only EF's MaxLength/required column constraints backing them — a duplicate
// username/email or a malformed email address surfaced as an unhandled 500, not a clean
// validation error). Mirrors the existing FluentValidation convention every other Create/Update
// request in this app already uses (see ProcessDefinitionValidators.cs).
public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(r => r.Username).NotEmpty().MaximumLength(256);
        RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(256);
        RuleFor(r => r.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(r => r.Password).NotEmpty().MinimumLength(8);
    }
}

public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(r => r.DisplayName).NotEmpty().MaximumLength(256);
        RuleFor(r => r.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(r => r.ExpectedVersion).NotEmpty();
    }
}

// Phase 9 — same password rule CreateUserRequestValidator already enforces (NotEmpty,
// MinimumLength(8)) — one password policy, not a second one for reset.
public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(r => r.NewPassword).NotEmpty().MinimumLength(8);
        RuleFor(r => r.ExpectedVersion).NotEmpty();
    }
}

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(r => r.CurrentPassword).NotEmpty();
        RuleFor(r => r.NewPassword).NotEmpty().MinimumLength(8);
    }
}
