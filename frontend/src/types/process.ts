// Mirrors BPM.Application.Processes.ProcessDefinitionDtos and BPM.Application.Workflow's
// validation DTOs on the backend — the current implementation is the source of truth, not
// Skill.md's original API contract sketch.

export type ProcessDefinitionStatus = 'Draft' | 'Published' | 'Suspended' | 'Archived';
export type ProcessVersionStatus = 'Draft' | 'Published';

// Phase 7.2.1 — mirrors BPM.Domain.Entities.ProcessInstanceStatus. Only Running/Completed/Rejected
// are ever actually reachable (confirmed by inspection — Cancelled/Suspended/Failed are reserved
// but no code path sets them); all four are still listed here since the DTO/enum itself could
// technically report them, and the UI must never crash on a value it doesn't have a color for.
export type ProcessInstanceStatus = 'Running' | 'Completed' | 'Rejected' | 'Cancelled' | 'Suspended' | 'Failed';

export interface ProcessDefinition {
  id: string;
  key: string;
  name: string;
  description: string | null;
  category: string | null;
  status: ProcessDefinitionStatus;
  currentVersionId: string | null;
  createdAt: string;
  createdBy: string | null;
  updatedAt: string | null;
  // Phase 8 — governance ownership; null means no owner assigned (only an Administrator may then
  // perform owner-gated governance actions).
  ownerUserId: string | null;
  // Base64 RowVersion — echo back as ProcessLifecycleActionRequest/AssignProcessOwnerRequest's
  // expectedVersion for Suspend/Archive/Restore/owner changes.
  rowVersion: string;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface ProcessDefinitionQuery {
  search?: string;
  status?: ProcessDefinitionStatus;
  page?: number;
  pageSize?: number;
}

export interface CreateProcessDefinitionRequest {
  key: string;
  name: string;
  description?: string | null;
  category?: string | null;
}

export interface UpdateProcessDefinitionRequest {
  name: string;
  description?: string | null;
  category?: string | null;
}

// --- Workflow definition graph (BPM.Domain.Workflow.WorkflowDefinition) ---

export type WorkflowNodeType =
  | 'Start'
  | 'End'
  | 'UserTask'
  | 'ApprovalTask'
  | 'ServiceTask'
  | 'ScriptTask'
  | 'ExclusiveGateway'
  | 'ParallelGateway'
  | 'JoinGateway'
  | 'Timer'
  | 'Notification'
  | 'SubProcess';

export type WorkflowAssignmentType =
  | 'User'
  | 'Role'
  | 'Department'
  | 'Manager'
  | 'DepartmentManager'
  | 'ProcessInitiator'
  | 'Dynamic';

export interface WorkflowAssignment {
  type: WorkflowAssignmentType;
  value: string;
}

export type ApprovalPolicy = 'Sequential' | 'All' | 'AnyOne';

export interface ApprovalConfig {
  policy: ApprovalPolicy;
  assignments: WorkflowAssignment[];
  allowReject?: boolean;
  allowReturn?: boolean;
  allowDelegate?: boolean;
  allowTransfer?: boolean;
  allowAddApprover?: boolean;
  returnPolicy?: { enabled: boolean; target?: 'PreviousUserTask' } | null;
}

export interface FormReference {
  formDefinitionKey: string;
  // Phase 10 — snapshotted server-side by WorkflowEngine.PublishVersionAsync at publish time; the
  // Designer never sets or edits this. Always null on a Draft version and on any ProcessVersion
  // published before Phase 10.
  formVersionId?: string | null;
}

export interface WorkflowNodeDefinition {
  id: string;
  type: WorkflowNodeType;
  name: string;
  assignment?: WorkflowAssignment | null;
  approval?: ApprovalConfig | null;
  form?: FormReference | null;
}

export interface WorkflowTransitionDefinition {
  id: string;
  source: string;
  target: string;
  name?: string | null;
}

export interface WorkflowDefinition {
  nodes: WorkflowNodeDefinition[];
  transitions: WorkflowTransitionDefinition[];
}

export interface ProcessVersion {
  id: string;
  processDefinitionId: string;
  versionNumber: number;
  status: ProcessVersionStatus;
  definition: WorkflowDefinition;
  createdAt: string;
  createdBy: string | null;
  publishedAt: string | null;
  publishedBy: string | null;
  // Phase 8 — optional governance note supplied at publish time; null for pre-Phase-8 versions.
  // Immutable once set, like the rest of a Published version.
  changeReason: string | null;
  // Base64 RowVersion (Skill.md §33) — echo back as UpdateProcessVersionRequest.expectedVersion
  // to save; a stale one is rejected with 409 PROCESS_VERSION_CONCURRENCY_CONFLICT (Phase 5.3.2).
  rowVersion: string;
}

export interface CreateProcessVersionRequest {
  definition: WorkflowDefinition;
}

export interface UpdateProcessVersionRequest {
  definition: WorkflowDefinition;
  expectedVersion: string;
}

export interface WorkflowValidationError {
  code: string;
  message: string;
}

export interface WorkflowValidationResult {
  isValid: boolean;
  errors: WorkflowValidationError[];
}

// --- Phase 8: Process Governance & Lifecycle ---

export interface PublishProcessRequest {
  changeReason?: string | null;
}

export interface ProcessLifecycleActionRequest {
  expectedVersion: string;
}

export interface AssignProcessOwnerRequest {
  ownerUserId: string | null;
  expectedVersion: string;
}

export interface NodeSummary {
  nodeId: string;
  name: string;
  type: WorkflowNodeType;
}

export interface ModifiedNode {
  nodeId: string;
  fromName: string;
  toName: string;
  changedFields: string[];
}

export interface TransitionSummary {
  transitionId: string;
  source: string;
  target: string;
}

export interface ModifiedTransition {
  transitionId: string;
  changedFields: string[];
}

export interface VersionComparisonResponse {
  processDefinitionId: string;
  fromVersionId: string;
  fromVersionNumber: number;
  toVersionId: string;
  toVersionNumber: number;
  addedNodes: NodeSummary[];
  removedNodes: NodeSummary[];
  modifiedNodes: ModifiedNode[];
  addedTransitions: TransitionSummary[];
  removedTransitions: TransitionSummary[];
  modifiedTransitions: ModifiedTransition[];
  summary: string;
}
