// Mirrors BPM.Application.Departments.DepartmentDto/CreateDepartmentRequest/UpdateDepartmentRequest.
export interface Department {
  id: string;
  name: string;
  organizationId: string;
  parentId: string | null;
  managerUserId: string | null;
  // Base64 RowVersion (Phase 5.5.2) — echo back as UpdateDepartmentRequest.expectedVersion to
  // save; a stale one is rejected with 409 DEPARTMENT_CONCURRENCY_CONFLICT.
  rowVersion: string;
}

export interface CreateDepartmentRequest {
  name: string;
  organizationId: string;
  parentId?: string | null;
  managerUserId?: string | null;
}

export interface UpdateDepartmentRequest {
  name: string;
  parentId: string | null;
  managerUserId: string | null;
  expectedVersion: string;
}
