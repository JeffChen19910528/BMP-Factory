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

public static class CurrentUserServiceExtensions
{
    // Every caller of this sits behind [Authorize], where the JWT middleware already guarantees
    // a NameIdentifier claim — so a null UserId here means the auth pipeline is misconfigured,
    // not a legitimate request state. Centralizing the "trust it's non-null past auth" judgment
    // call here means controllers use one safe accessor instead of repeating `UserId!.Value`
    // (unsafe, easy to copy onto an endpoint that isn't actually behind [Authorize]) everywhere.
    public static Guid RequireUserId(this ICurrentUserService currentUser) =>
        currentUser.UserId ?? throw new InvalidOperationException(
            "ICurrentUserService.UserId was null on an authenticated request. This endpoint must be behind [Authorize].");
}
