using BPM.Application.Auth;
using BPM.Application.Common;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BPM.Identity;

public class AuthService : IAuthService
{
    private readonly BpmDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly IAuditService _auditService;
    private readonly JwtSettings _jwtSettings;

    public AuthService(
        BpmDbContext db,
        IPasswordHasher<User> passwordHasher,
        IJwtTokenService jwtTokenService,
        IAuditService auditService,
        IOptions<JwtSettings> jwtSettings)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwtTokenService = jwtTokenService;
        _auditService = auditService;
        _jwtSettings = jwtSettings.Value;
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(u => u.Username == request.Username, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return null;
        }

        var result = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        var roles = user.UserRoles.Select(ur => ur.Role!.Name).ToArray();
        var token = _jwtTokenService.GenerateToken(user, roles);
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpiryMinutes);

        // Phase 9 — metadata only, set on every successful login. Never backfilled from AuditLog
        // history for existing users; simply NULL until their first post-Phase-9 login. A direct
        // ExecuteUpdateAsync (not a tracked-entity SaveChangesAsync) deliberately bypasses User's
        // RowVersion concurrency token — two concurrent logins for the same account racing on
        // this field is not a real conflict worth failing the whole login for (found live: two
        // parallel logins as the same user threw DbUpdateConcurrencyException and 500'd the
        // login itself before this fix). Deliberately does NOT also set `user.LastLoginAt` on the
        // tracked entity — LoginResponse never carries it, and mutating the tracked property here
        // would re-dirty `user` and hit the same RowVersion race on the audit log's own
        // SaveChangesAsync call two lines below (this was the actual first failure found live,
        // not the ExecuteUpdateAsync call itself).
        await _db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAt, DateTime.UtcNow), cancellationToken);

        await _auditService.LogAsync(AuditActions.Login, nameof(User), user.Id.ToString(), cancellationToken: cancellationToken);

        return new LoginResponse(token, expiresAt, user.Id, user.DisplayName, roles);
    }
}
