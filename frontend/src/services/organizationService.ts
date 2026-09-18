import { apiClient } from './apiClient';
import type { CreateOrganizationRequest, Organization, UpdateOrganizationRequest } from '../types/organization';

export async function listOrganizations(): Promise<Organization[]> {
  const response = await apiClient.get<Organization[]>('/api/organizations');
  return response.data;
}

export async function createOrganization(request: CreateOrganizationRequest): Promise<Organization> {
  const response = await apiClient.post<Organization>('/api/organizations', request);
  return response.data;
}

// Phase 9 — Organization gained a real Update endpoint (previously read-only/Create-only).
export async function updateOrganization(id: string, request: UpdateOrganizationRequest): Promise<Organization> {
  const response = await apiClient.put<Organization>(`/api/organizations/${id}`, request);
  return response.data;
}
