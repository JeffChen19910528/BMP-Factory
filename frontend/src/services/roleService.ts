import { apiClient } from './apiClient';
import type { AssignRoleRequest, CreateRoleRequest, Role, UnassignRoleRequest, UpdateRoleRequest } from '../types/role';
import type { User } from '../types/user';

// GET /api/roles is Administrator-only on the backend (RolesController's whole class is
// [Authorize(Roles = "Administrator")]) — fine here, since only Administrators can reach the
// Designer's Properties Panel in the first place (creating/editing a ProcessVersion is already
// Administrator-only).
export async function listRoles(): Promise<Role[]> {
  const response = await apiClient.get<Role[]>('/api/roles');
  return response.data;
}

export async function createRole(request: CreateRoleRequest): Promise<Role> {
  const response = await apiClient.post<Role>('/api/roles', request);
  return response.data;
}

// Phase 9 — rename only. Renaming the literal "Administrator" role is rejected server-side
// (409 CANNOT_RENAME_ADMINISTRATOR_ROLE).
export async function updateRole(id: string, request: UpdateRoleRequest): Promise<Role> {
  const response = await apiClient.put<Role>(`/api/roles/${id}`, request);
  return response.data;
}

export async function getRoleMembers(roleId: string): Promise<User[]> {
  const response = await apiClient.get<User[]>(`/api/roles/${roleId}/members`);
  return response.data;
}

export async function assignRole(request: AssignRoleRequest): Promise<void> {
  await apiClient.post('/api/roles/assign', request);
}

export async function unassignRole(request: UnassignRoleRequest): Promise<void> {
  await apiClient.post('/api/roles/unassign', request);
}
