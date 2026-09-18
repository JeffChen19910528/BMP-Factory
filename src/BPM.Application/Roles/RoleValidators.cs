using FluentValidation;

namespace BPM.Application.Roles;

// Phase 9 Part 22/23 — Role rename. Mirrors CreateRoleRequest's own implicit length limit
// (Role.Name is HasMaxLength(128) in RoleConfiguration).
public class UpdateRoleRequestValidator : AbstractValidator<UpdateRoleRequest>
{
    public UpdateRoleRequestValidator()
    {
        RuleFor(r => r.Name).NotEmpty().MaximumLength(128);
        RuleFor(r => r.ExpectedVersion).NotEmpty();
    }
}
