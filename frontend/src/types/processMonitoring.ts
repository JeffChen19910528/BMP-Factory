import type { ProcessInstanceStatus } from './process';
import type { Task, TaskInstanceStatus, TaskSla } from './task';

// Phase 7.2.1 — mirrors BPM.Application.ProcessMonitoring.ProcessMonitoringDtos exactly. The
// backend is the sole authority for Status/SlaStatus/CurrentTask* — this file only shapes the
// response, it never computes anything (no client-side Overdue/current-task derivation anywhere
// in this feature).
export type ProcessMonitoringSlaStatus = 'Active' | 'Warning' | 'Overdue' | 'Completed';

export interface ProcessMonitoringItem {
  processInstanceId: string;
  processDefinitionId: string;
  processDefinitionKey: string;
  processDefinitionName: string;
  status: ProcessInstanceStatus;
  initiatorId: string;
  initiatorDisplayName: string;
  startedAt: string;
  updatedAt: string;
  currentTaskId: string | null;
  currentTaskName: string | null;
  currentTaskStatus: TaskInstanceStatus | null;
  currentTaskIsApprovalTask: boolean;
  currentTaskAssigneeDisplay: string | null;
  activeTaskCount: number;
  slaStatus: ProcessMonitoringSlaStatus | null;
  slaDueAt: string | null;
}

export type ProcessMonitoringSortBy = 'UpdatedAt' | 'StartedAt' | 'Status' | 'ProcessDefinitionName' | 'SlaDueAt';
export type SortDirection = 'Ascending' | 'Descending';

export interface ProcessMonitoringQuery {
  search?: string;
  status?: ProcessInstanceStatus;
  processDefinitionId?: string;
  initiatorId?: string;
  slaStatus?: ProcessMonitoringSlaStatus;
  startedFrom?: string;
  startedTo?: string;
  sortBy?: ProcessMonitoringSortBy;
  sortDirection?: SortDirection;
  page?: number;
  pageSize?: number;
}

// Phase 7.2.2 — Process Instance Detail + Timeline. Mirrors
// BPM.Application.ProcessMonitoring.ProcessInstanceDetailDtos exactly — reuses the existing `Task`
// and `TaskSla` types (Task History/Approval/per-task SLA are literally the same `Task[]` the
// Process Monitoring list and Task Detail already use; SlaSummary is literally `CurrentTask.sla`)
// rather than inventing parallel shapes.
export type ProcessProgressState = 'Completed' | 'Current' | 'Pending';

export interface ProcessProgressItem {
  nodeId: string;
  nodeType: string;
  displayName: string;
  state: ProcessProgressState;
}

export interface ProcessTimelineItem {
  timestamp: string;
  eventType: string;
  title: string;
  description: string | null;
  sourceId: string | null;
  actorId: string | null;
  actorDisplayName: string | null;
}

export interface ProcessInstanceDetail {
  processInstanceId: string;
  processDefinitionId: string;
  processDefinitionKey: string;
  processDefinitionName: string;
  status: ProcessInstanceStatus;
  initiatorId: string;
  initiatorDisplayName: string;
  startedAt: string;
  completedAt: string | null;
  activeTaskCount: number;
  currentTask: Task | null;
  tasks: Task[];
  slaSummary: TaskSla | null;
  // Phase 7.2.3 hardening: slaSummary.status is the raw TaskSlaStatus (no 'Warning' value exists
  // there). slaStatus is the derived bucket — the same one Process Monitoring's list and
  // Dashboard already show — so this page never disagrees with them for the same task.
  slaStatus: ProcessMonitoringSlaStatus | null;
  workflowProgress: ProcessProgressItem[];
  timeline: ProcessTimelineItem[];
}
