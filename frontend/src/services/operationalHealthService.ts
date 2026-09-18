import { apiClient } from './apiClient';
import type { OperationalHealthResponse } from '../types/operationalHealth';

// Administrator-only on the backend.
export async function getOperationalHealth(): Promise<OperationalHealthResponse> {
  const response = await apiClient.get<OperationalHealthResponse>('/api/operational-health');
  return response.data;
}
