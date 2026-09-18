import { apiClient } from './apiClient';
import type { ChangePasswordRequest, CreateUserRequest, ResetPasswordRequest, UpdateUserRequest, User } from '../types/user';

export async function listUsers(): Promise<User[]> {
  const response = await apiClient.get<User[]>('/api/users');
  return response.data;
}

export async function createUser(request: CreateUserRequest): Promise<User> {
  const response = await apiClient.post<User>('/api/users', request);
  return response.data;
}

export async function updateUser(id: string, request: UpdateUserRequest): Promise<User> {
  const response = await apiClient.put<User>(`/api/users/${id}`, request);
  return response.data;
}

// Phase 9 — Administrator-initiated password reset. The response is the updated User (never the
// password or its hash — the backend's UserDto has no such field to begin with).
export async function resetUserPassword(id: string, request: ResetPasswordRequest): Promise<User> {
  const response = await apiClient.post<User>(`/api/users/${id}/reset-password`, request);
  return response.data;
}

// Phase 9 — self-service. No user id parameter — the backend resolves the caller from the JWT.
export async function changeMyPassword(request: ChangePasswordRequest): Promise<void> {
  await apiClient.post('/api/users/me/change-password', request);
}
