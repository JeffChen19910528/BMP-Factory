namespace BPM.Application.Users;

// Phase 5.5.2: RowVersion added — User already extended AuditableEntity and already had a
// RowVersion column configured as a concurrency token (Phase 1), but no service ever surfaced or
// checked it, so two administrators editing the same user concurrently silently last-write-won.
// Wired up using the exact same base64 RowVersion / ExpectedVersion pattern Phase 5.3.2/5.4.1
// already established for ProcessVersion/FormVersion — see UserService.UpdateAsync.
// Phase 9: LastLoginAt added — metadata only, set exclusively by AuthService.LoginAsync on every
// successful login; NULL for a user who has never logged in since this field was introduced.
public record UserDto(Guid Id, string Username, string DisplayName, string Email, Guid? DepartmentId, bool IsActive, DateTime? LastLoginAt, string RowVersion);

public record CreateUserRequest(string Username, string DisplayName, string Email, string Password, Guid? DepartmentId);

public record UpdateUserRequest(string DisplayName, string Email, Guid? DepartmentId, bool IsActive, string ExpectedVersion);

// Phase 9 Part 12/13/14 — Administrator-initiated password reset. No email/token workflow (an
// Administrator directly sets a new password for a user who can no longer authenticate any other
// way) — deliberately the smallest thing that closes the "no password recovery path exists at
// all" gap identified in this phase's own Discovery. ExpectedVersion follows the exact same
// convention UpdateUserRequest already uses — a stale reset is rejected, never silently applied
// over a concurrent edit.
public record ResetPasswordRequest(string NewPassword, string ExpectedVersion);

// Phase 9 Part 17/18 — self-service. The caller's identity always comes from
// ICurrentUserService/JWT (never a client-supplied UserId) — this request carries no user
// identifier at all, the same "no recipient/subject parameter" pattern NotificationsController
// already established. CurrentPassword is verified with the same IPasswordHasher already used by
// AuthService.LoginAsync before NewPassword is ever applied.
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
