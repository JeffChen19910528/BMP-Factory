// Mirrors BPM.Application.Organizations.OrganizationDto/CreateOrganizationRequest/
// UpdateOrganizationRequest. Phase 5.5.2 deliberately left this as a read-only picker source with
// no Update endpoint at all ("nothing here needs more than an existing GET list to pick from").
// Phase 9 closes that gap — Organization now has real Update/RowVersion/audit, matching
// Department's own established shape.
export interface Organization {
  id: string;
  name: string;
  parentId: string | null;
  // Base64 RowVersion — echo back as UpdateOrganizationRequest.expectedVersion to save; a stale
  // one is rejected with 409 ORGANIZATION_CONCURRENCY_CONFLICT.
  rowVersion: string;
}

export interface CreateOrganizationRequest {
  name: string;
  parentId?: string | null;
}

export interface UpdateOrganizationRequest {
  name: string;
  parentId: string | null;
  expectedVersion: string;
}
