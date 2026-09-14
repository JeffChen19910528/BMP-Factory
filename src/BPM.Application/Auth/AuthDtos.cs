namespace BPM.Application.Auth;

public record LoginRequest(string Username, string Password);

public record LoginResponse(string AccessToken, DateTime ExpiresAt, Guid UserId, string DisplayName, IReadOnlyCollection<string> Roles);
