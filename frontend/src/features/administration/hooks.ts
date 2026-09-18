import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryAuditLogs } from '../../services/auditLogService';
import { createDepartment, listDepartments, updateDepartment } from '../../services/departmentService';
import { getOperationalHealth } from '../../services/operationalHealthService';
import { createOrganization, listOrganizations, updateOrganization } from '../../services/organizationService';
import { assignRole, createRole, getRoleMembers, listRoles, unassignRole, updateRole } from '../../services/roleService';
import { createSlaPolicy, listSlaPolicies, updateSlaPolicy } from '../../services/slaPolicyService';
import { changeMyPassword, createUser, listUsers, resetUserPassword, updateUser } from '../../services/userService';
import type { AuditLogQuery } from '../../types/audit';
import type { CreateDepartmentRequest, UpdateDepartmentRequest } from '../../types/department';
import type { CreateOrganizationRequest, UpdateOrganizationRequest } from '../../types/organization';
import type { AssignRoleRequest, CreateRoleRequest, UnassignRoleRequest, UpdateRoleRequest } from '../../types/role';
import type { CreateSlaPolicyRequest, UpdateSlaPolicyRequest } from '../../types/slaPolicy';
import type { ChangePasswordRequest, CreateUserRequest, ResetPasswordRequest, UpdateUserRequest } from '../../types/user';

// Centralizes TanStack Query keys/hooks for the Administration workspace (Users/Departments/
// Roles/Audit Logs), mirroring features/process/hooks.ts's pattern — pages call these instead of
// the service functions directly. Users/Departments/Roles are "fetch everything, paginate/filter
// client-side in AntD Table" (matching how useUsersById/useDepartments already work for the
// Process Designer's assignment pickers) — Audit Logs is the one resource with real server-side
// pagination, since AuditLogQueryService already had the query-param groundwork this phase
// extended with TotalCount (see AuditLogDtos.cs's doc comment).
export const administrationKeys = {
  users: ['administration', 'users'] as const,
  departments: ['administration', 'departments'] as const,
  organizations: ['administration', 'organizations'] as const,
  roles: ['administration', 'roles'] as const,
  roleMembers: (roleId: string) => ['administration', 'roles', roleId, 'members'] as const,
  auditLogs: (query: AuditLogQuery) => ['administration', 'audit-logs', query] as const,
  slaPolicies: ['administration', 'sla-policies'] as const,
  operationalHealth: ['administration', 'operational-health'] as const,
};

// --- Users ---

export function useUsers() {
  return useQuery({ queryKey: administrationKeys.users, queryFn: listUsers });
}

export function useCreateUser() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateUserRequest) => createUser(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.users });
    },
  });
}

export function useUpdateUser(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: UpdateUserRequest) => updateUser(id, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.users });
    },
  });
}

// Phase 9 — Administrator-initiated password reset.
export function useResetUserPassword(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: ResetPasswordRequest) => resetUserPassword(id, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.users });
    },
  });
}

// Phase 9 — self-service; not scoped to the Administration workspace's own query keys since it
// never invalidates the (Administrator-only) Users list — a normal user has no reason to ever
// hold that query's cache.
export function useChangeMyPassword() {
  return useMutation({
    mutationFn: (request: ChangePasswordRequest) => changeMyPassword(request),
  });
}

// --- Departments ---

export function useDepartments() {
  return useQuery({ queryKey: administrationKeys.departments, queryFn: listDepartments });
}

export function useOrganizations() {
  return useQuery({ queryKey: administrationKeys.organizations, queryFn: listOrganizations });
}

// Phase 9 — Organization gained real Create/Update mutations (previously read-only/Create-only).
export function useCreateOrganization() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateOrganizationRequest) => createOrganization(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.organizations });
    },
  });
}

export function useUpdateOrganization(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: UpdateOrganizationRequest) => updateOrganization(id, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.organizations });
    },
  });
}

export function useCreateDepartment() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateDepartmentRequest) => createDepartment(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.departments });
    },
  });
}

export function useUpdateDepartment(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: UpdateDepartmentRequest) => updateDepartment(id, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.departments });
    },
  });
}

// --- Roles ---

export function useRoles() {
  return useQuery({ queryKey: administrationKeys.roles, queryFn: listRoles });
}

export function useRoleMembers(roleId: string | undefined) {
  return useQuery({
    queryKey: administrationKeys.roleMembers(roleId ?? ''),
    queryFn: () => getRoleMembers(roleId!),
    enabled: !!roleId,
  });
}

export function useCreateRole() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateRoleRequest) => createRole(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.roles });
    },
  });
}

export function useAssignRole(roleId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: AssignRoleRequest) => assignRole(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.roles });
      queryClient.invalidateQueries({ queryKey: administrationKeys.roleMembers(roleId) });
    },
  });
}

export function useUnassignRole(roleId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: UnassignRoleRequest) => unassignRole(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.roles });
      queryClient.invalidateQueries({ queryKey: administrationKeys.roleMembers(roleId) });
    },
  });
}

// Phase 9 — rename only.
export function useUpdateRole(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: UpdateRoleRequest) => updateRole(id, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.roles });
    },
  });
}

// --- SLA Policies ---

export function useSlaPolicies() {
  return useQuery({ queryKey: administrationKeys.slaPolicies, queryFn: listSlaPolicies });
}

export function useCreateSlaPolicy() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateSlaPolicyRequest) => createSlaPolicy(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.slaPolicies });
    },
  });
}

export function useUpdateSlaPolicy(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: UpdateSlaPolicyRequest) => updateSlaPolicy(id, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: administrationKeys.slaPolicies });
    },
  });
}

// --- Operational Health ---

export function useOperationalHealth() {
  return useQuery({ queryKey: administrationKeys.operationalHealth, queryFn: getOperationalHealth });
}

// --- Audit Logs ---

export function useAuditLogs(query: AuditLogQuery) {
  return useQuery({
    queryKey: administrationKeys.auditLogs(query),
    queryFn: () => queryAuditLogs(query),
    placeholderData: (previous) => previous,
  });
}
