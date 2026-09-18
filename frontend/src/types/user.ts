// Mirrors BPM.Application.Users.UserDto/CreateUserRequest/UpdateUserRequest.
export interface User {
  id: string;
  username: string;
  displayName: string;
  email: string;
  departmentId: string | null;
  isActive: boolean;
  // Phase 9 — set on every successful login; null until a user's first post-Phase-9 login.
  lastLoginAt: string | null;
  // Base64 RowVersion (Phase 5.5.2) — echo back as UpdateUserRequest.expectedVersion to save; a
  // stale one is rejected with 409 USER_CONCURRENCY_CONFLICT.
  rowVersion: string;
}

export interface CreateUserRequest {
  username: string;
  displayName: string;
  email: string;
  password: string;
  departmentId?: string | null;
}

export interface UpdateUserRequest {
  displayName: string;
  email: string;
  departmentId: string | null;
  isActive: boolean;
  expectedVersion: string;
}

// Phase 9 — Administrator-initiated password reset.
export interface ResetPasswordRequest {
  newPassword: string;
  expectedVersion: string;
}

// Phase 9 — self-service. No user identifier — the caller is always the authenticated user.
export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}
