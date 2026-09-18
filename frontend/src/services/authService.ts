import { apiClient } from './apiClient';
import type { LoginRequest, LoginResponse } from '../types/auth';

export async function login(request: LoginRequest): Promise<LoginResponse> {
  const response = await apiClient.post<LoginResponse>('/api/auth/login', request);
  return response.data;
}
