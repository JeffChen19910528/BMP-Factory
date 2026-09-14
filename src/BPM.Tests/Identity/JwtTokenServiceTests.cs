using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BPM.Domain.Entities;
using BPM.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace BPM.Tests.Identity;

public class JwtTokenServiceTests
{
    private static JwtTokenService CreateService() =>
        new(Options.Create(new JwtSettings
        {
            Secret = "unit-test-secret-key-at-least-32-bytes-long",
            Issuer = "bpm-tests",
            Audience = "bpm-tests",
            ExpiryMinutes = 30,
        }));

    [Fact]
    public void GenerateToken_IncludesUserIdAndRoleClaims()
    {
        var service = CreateService();
        var user = new User { Username = "alice", Email = "alice@example.com" };

        var token = service.GenerateToken(user, new[] { "Administrator", "Manager" });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(user.Id.ToString(), jwt.Claims.Single(c => c.Type == ClaimTypes.NameIdentifier).Value);
        Assert.Equal(new[] { "Administrator", "Manager" }, jwt.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value));
        Assert.Equal("bpm-tests", jwt.Issuer);
    }

    [Fact]
    public void GenerateToken_SetsExpiryFromSettings()
    {
        var service = CreateService();
        var user = new User { Username = "bob", Email = "bob@example.com" };

        var token = service.GenerateToken(user, Array.Empty<string>());

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var expectedExpiry = DateTime.UtcNow.AddMinutes(30);
        Assert.True(Math.Abs((jwt.ValidTo - expectedExpiry).TotalMinutes) < 1);
    }
}
