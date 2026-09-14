using System.Security.Claims;
using BPM.Application.Common;
using Microsoft.AspNetCore.Http;

namespace BPM.Infrastructure.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private HttpContext? Context => _httpContextAccessor.HttpContext;

    public Guid? UserId
    {
        get
        {
            var value = Context?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    // Single-tenant v1: always Guid.Empty (Skill.md §43). Swap to a claim lookup once
    // multi-tenant onboarding exists.
    public Guid TenantId => Guid.Empty;

    public IReadOnlyCollection<string> Roles =>
        Context?.User?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray() ?? Array.Empty<string>();

    public string? IpAddress => Context?.Connection?.RemoteIpAddress?.ToString();

    public string? UserAgent => Context?.Request?.Headers["User-Agent"].ToString();
}
