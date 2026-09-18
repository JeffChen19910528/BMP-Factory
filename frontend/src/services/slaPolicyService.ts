import { apiClient } from './apiClient';
import type { CreateSlaPolicyRequest, SlaPolicy, UpdateSlaPolicyRequest } from '../types/slaPolicy';

// GET /api/sla-policies is Administrator-only on the backend (SlaPoliciesController's whole
// class is [Authorize(Roles = "Administrator")], matching Roles/AuditLogs' own precedent — no
// legitimate non-admin consumer for policy configuration exists).
export async function listSlaPolicies(): Promise<SlaPolicy[]> {
  const response = await apiClient.get<SlaPolicy[]>('/api/sla-policies');
  return response.data;
}

export async function createSlaPolicy(request: CreateSlaPolicyRequest): Promise<SlaPolicy> {
  const response = await apiClient.post<SlaPolicy>('/api/sla-policies', request);
  return response.data;
}

export async function updateSlaPolicy(id: string, request: UpdateSlaPolicyRequest): Promise<SlaPolicy> {
  const response = await apiClient.put<SlaPolicy>(`/api/sla-policies/${id}`, request);
  return response.data;
}
