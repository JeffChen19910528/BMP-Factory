import { apiClient } from './apiClient';
import type { DashboardResponse } from '../types/dashboard';

// No parameters at all — the server derives scope entirely from the authenticated caller
// (ICurrentUserService). There is deliberately no userId/scope argument here for a caller to even
// attempt passing through as a query parameter.
export async function getDashboard(): Promise<DashboardResponse> {
  const response = await apiClient.get<DashboardResponse>('/api/dashboard');
  return response.data;
}
