namespace BPM.Application.Users;

public record UserDto(Guid Id, string Username, string DisplayName, string Email, Guid? DepartmentId, bool IsActive);

public record CreateUserRequest(string Username, string DisplayName, string Email, string Password, Guid? DepartmentId);

public record UpdateUserRequest(string DisplayName, string Email, Guid? DepartmentId, bool IsActive);
