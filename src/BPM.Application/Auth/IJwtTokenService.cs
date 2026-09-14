using BPM.Domain.Entities;

namespace BPM.Application.Auth;

public interface IJwtTokenService
{
    string GenerateToken(User user, IEnumerable<string> roles);
}
