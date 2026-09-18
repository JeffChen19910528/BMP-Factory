namespace BPM.Application.Users;

public interface IUserService
{
    Task<IReadOnlyList<UserDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<UserDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default);
    Task<UserDto?> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken = default);

    // Phase 9 — Administrator-only (enforced by the controller); returns null if the target user
    // doesn't exist, matching UpdateAsync's own not-found convention.
    Task<UserDto?> ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken cancellationToken = default);

    // Phase 9 — self-service; userId always comes from the authenticated caller (controller passes
    // ICurrentUserService.RequireUserId(), never a client-supplied id).
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken = default);
}
