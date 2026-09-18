using BPM.Application.Auth;
using BPM.Application.Common;
using BPM.Domain.Entities;
using BPM.Identity;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using BPM.Tests.Workflow;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace BPM.Tests.Identity;

// Phase 9 — AuthService.LoginAsync now stamps User.LastLoginAt on every successful login. This
// file covers that addition; login itself had no dedicated unit test file before (only
// live/E2E coverage), so basic success/failure behavior is asserted here too rather than
// assumed still correct.
[Collection("Postgres")]
public class AuthServiceTests
{
    private static AuthService NewService(BpmDbContext db) =>
        new(db, new PasswordHasher<User>(), new JwtTokenService(Options.Create(new JwtSettings
        {
            Secret = "unit-test-secret-key-at-least-32-bytes-long",
            Issuer = "bpm-tests",
            Audience = "bpm-tests",
            ExpiryMinutes = 30,
        })), new AuditService(db, new FixedCurrentUser(Guid.Empty)), Options.Create(new JwtSettings
        {
            Secret = "unit-test-secret-key-at-least-32-bytes-long",
            Issuer = "bpm-tests",
            Audience = "bpm-tests",
            ExpiryMinutes = 30,
        }));

    private static async Task<(string Username, string Password)> SeedUserAsync(BpmDbContext db)
    {
        var username = $"auth-{Guid.NewGuid():N}";
        const string password = "Passw0rd!123";
        var user = new User
        {
            Username = username,
            DisplayName = "Auth Test User",
            Email = $"{username}@bpm-tests.local",
            IsActive = true,
        };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (username, password);
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_SetsLastLoginAt()
    {
        await using var seedDb = PostgresFixture.CreateContext();
        var (username, password) = await SeedUserAsync(seedDb);

        await using var loginDb = PostgresFixture.CreateContext();
        var response = await NewService(loginDb).LoginAsync(new LoginRequest(username, password));

        Assert.NotNull(response);

        await using var verifyDb = PostgresFixture.CreateContext();
        var stored = await verifyDb.Users.SingleAsync(u => u.Username == username);
        Assert.NotNull(stored.LastLoginAt);
    }

    [Fact]
    public async Task LoginAsync_InvalidPassword_ReturnsNull()
    {
        await using var seedDb = PostgresFixture.CreateContext();
        var (username, _) = await SeedUserAsync(seedDb);

        await using var loginDb = PostgresFixture.CreateContext();
        var response = await NewService(loginDb).LoginAsync(new LoginRequest(username, "WrongPassword!123"));

        Assert.Null(response);
    }

    // Regression test for a real bug found live: two concurrent logins as the same user used to
    // both load the User row, both set LastLoginAt, and race on SaveChangesAsync's RowVersion
    // concurrency check — the loser threw DbUpdateConcurrencyException and 500'd instead of
    // logging in successfully. LastLoginAt is now written via a direct ExecuteUpdateAsync that
    // bypasses the concurrency token entirely (see AuthService.LoginAsync's own comment).
    [Fact]
    public async Task LoginAsync_ConcurrentLoginsForSameUser_BothSucceed()
    {
        await using var seedDb = PostgresFixture.CreateContext();
        var (username, password) = await SeedUserAsync(seedDb);

        await using var db1 = PostgresFixture.CreateContext();
        await using var db2 = PostgresFixture.CreateContext();

        var login1 = NewService(db1).LoginAsync(new LoginRequest(username, password));
        var login2 = NewService(db2).LoginAsync(new LoginRequest(username, password));

        var results = await Task.WhenAll(login1, login2);

        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
    }

    private class FixedCurrentUser : ICurrentUserService
    {
        public FixedCurrentUser(Guid? userId) => UserId = userId;
        public Guid? UserId { get; }
        public Guid TenantId => Guid.Empty;
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}
