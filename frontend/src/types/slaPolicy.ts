// Mirrors BPM.Application.Sla.SlaDtos' SlaPolicyDto/CreateSlaPolicyRequest/
// UpdateSlaPolicyRequest exactly — the backend has had full CRUD since Phase 6.3; this is the
// first frontend surface for it (Phase 9).
export interface SlaPolicy {
  id: string;
  processDefinitionId: string;
  nodeId: string;
  enabled: boolean;
  durationMinutes: number;
  warningOffsetMinutes: number;
  // Base64 RowVersion — echo back as UpdateSlaPolicyRequest.expectedVersion; a stale one is
  // rejected with 409 SLA_POLICY_CONCURRENCY_CONFLICT.
  rowVersion: string;
}

export interface CreateSlaPolicyRequest {
  processDefinitionId: string;
  nodeId: string;
  enabled: boolean;
  durationMinutes: number;
  warningOffsetMinutes: number;
}

export interface UpdateSlaPolicyRequest {
  enabled: boolean;
  durationMinutes: number;
  warningOffsetMinutes: number;
  expectedVersion: string;
}
