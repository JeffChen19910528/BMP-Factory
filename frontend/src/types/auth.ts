// Mirrors BPM.Application.Auth.AuthDtos on the backend (src/BPM.Application/Auth/AuthDtos.cs).
export interface LoginRequest {
  username: string;
  password: string;
}

export interface LoginResponse {
  accessToken: string;
  expiresAt: string;
  userId: string;
  displayName: string;
  roles: string[];
}

export interface AuthenticatedUser {
  userId: string;
  displayName: string;
  roles: string[];
}
