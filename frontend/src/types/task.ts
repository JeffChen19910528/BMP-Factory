// Mirrors BPM.Application.Workflow.TaskDto / ApprovalSummaryDto / ApprovalAssignmentDto — the
// current implementation is the source of truth, not Skill.md's original contract sketch.

export type TaskInstanceStatus = 'Pending' | 'InProgress' | 'Completed' | 'Rejected' | 'Returned' | 'Cancelled' | 'Expired';
export type ApprovalPolicy = 'Sequential' | 'All' | 'AnyOne';
export type ApprovalInstanceStatus = 'Pending' | 'Approved' | 'Rejected';
export type ApprovalAssignmentStatus = 'Pending' | 'Approved' | 'Rejected' | 'Skipped';

export interface ApprovalAssignment {
  id: string;
  userId: string;
  delegatedToUserId: string | null;
  order: number;
  status: ApprovalAssignmentStatus;
  assignedAt: string;
  completedAt: string | null;
}

export interface ApprovalSummary {
  policy: ApprovalPolicy;
  status: ApprovalInstanceStatus;
  requiredCount: number;
  approvedCount: number;
  rejectedCount: number;
  assignments: ApprovalAssignment[];
}

// Phase 6.3 — SLA Foundation. Mirrors BPM.Application.Sla.TaskSlaDto — rides along on the
// existing, already-authorized TaskDto rather than a separate SLA endpoint (no PolicyId/policy
// internals exposed here — see TaskSlaDto's own comment).
// Phase 6.4 adds 'Overdue' — a real, backend-authoritative status set by SlaSchedulerWorker, never
// computed client-side (there is no "is this overdue" calculation anywhere in this frontend).
export type TaskSlaStatus = 'Active' | 'Completed' | 'Cancelled' | 'Overdue';

export interface TaskSla {
  startedAt: string;
  warningAt: string;
  dueAt: string;
  completedAt: string | null;
  status: TaskSlaStatus;
}

export interface Task {
  id: string;
  processInstanceId: string;
  nodeId: string;
  nodeName: string;
  assigneeId: string | null;
  assigneeRole: string | null;
  status: TaskInstanceStatus;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  dueAt: string | null;
  approval: ApprovalSummary | null;
  sla: TaskSla | null;
}

export interface DelegateTaskRequest {
  delegateToUserId: string;
}

export interface TransferTaskRequest {
  newUserId: string;
  reason?: string | null;
}

// Phase 5.5.1 — Approvals Worklist. Mirrors BPM.Application.Workflow.ApprovalWorklistItemDto — a
// read-only projection widening TaskDto's fields with the process/applicant context a worklist
// row needs (process name/key, applicant id), not a second task model.
export interface ApprovalWorklistItem {
  taskId: string;
  processInstanceId: string;
  processDefinitionKey: string;
  processDefinitionName: string;
  taskName: string;
  applicantId: string;
  assigneeId: string | null;
  assigneeRole: string | null;
  taskStatus: TaskInstanceStatus;
  createdAt: string;
  updatedAt: string;
  dueAt: string | null;
  approval: ApprovalSummary | null;
}

export interface ApprovalWorklistQuery {
  search?: string;
  status?: TaskInstanceStatus;
  page?: number;
  pageSize?: number;
}
