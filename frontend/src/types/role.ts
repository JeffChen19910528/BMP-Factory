// Mirrors BPM.Application.Roles.RoleDto/CreateRoleRequest/AssignRoleRequest/UnassignRoleRequest.
export interface Role {
  id: string;
  name: string;
  // Added Phase 5.5.2 — role membership was previously invisible from the frontend.
  memberCount: number;
  // Phase 9 — echo back as UpdateRoleRequest.expectedVersion to rename; a stale one is rejected
  // with 409 ROLE_CONCURRENCY_CONFLICT.
  rowVersion: string;
}

export interface CreateRoleRequest {
  name: string;
}

// Phase 9 — rename only. Renaming the literal "Administrator" role is rejected server-side
// (409 CANNOT_RENAME_ADMINISTRATOR_ROLE) — this frontend must not assume a hidden/disabled
// control is what enforces that; the backend is authoritative.
export interface UpdateRoleRequest {
  name: string;
  expectedVersion: string;
}

export interface AssignRoleRequest {
  userId: string;
  roleId: string;
}

export interface UnassignRoleRequest {
  userId: string;
  roleId: string;
}
