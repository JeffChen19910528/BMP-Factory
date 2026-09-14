namespace BPM.Application.Common;

// Backend-authoritative identity for the current request — never trust client-supplied
// UserId/RoleId/DepartmentId (Skill.md §27). Populated from the validated JWT only.
public interface ICurrentUserService
{
    Guid? UserId { get; }
    Guid TenantId { get; }
    IReadOnlyCollection<string> Roles { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
}
