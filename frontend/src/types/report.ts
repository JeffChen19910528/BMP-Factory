import type { ProcessInstanceStatus } from './process';
import type { ProcessMonitoringItem, ProcessMonitoringSlaStatus, ProcessMonitoringSortBy, SortDirection } from './processMonitoring';

// Phase 7.3 — Reporting. Mirrors BPM.Application.Reports.ReportDtos exactly. This page never
// computes Process/Task/Approval/SLA aggregates itself — every number here comes straight from
// GET /api/reports/summary (same "backend truth only" convention Process Monitoring/Dashboard
// already established).

export interface ReportProcessSummary {
  total: number;
  running: number;
  completed: number;
  rejected: number;
}

export interface ReportProcessBreakdownItem {
  processDefinitionId: string;
  processDefinitionKey: string;
  processDefinitionName: string;
  total: number;
  running: number;
  completed: number;
  rejected: number;
}

// Mirrors TaskInstanceStatus's 7 real persisted values 1:1 — no re-bucketing.
export interface ReportTaskSummary {
  total: number;
  pending: number;
  inProgress: number;
  completed: number;
  rejected: number;
  returned: number;
  cancelled: number;
  expired: number;
}

// Mirrors ApprovalAssignmentStatus's 5 real persisted values 1:1. Current assignment state only —
// the backend keeps no separate approval-action-history table (see ReportSlaSummary's sibling doc
// comment on ReportApprovalSummaryDto for the full explanation); do not read "approved" as
// "approve actions ever taken."
export interface ReportApprovalSummary {
  total: number;
  pending: number;
  approved: number;
  rejected: number;
  returned: number;
  cancelled: number;
}

// SLA compliance definition (see BPM.Application.Reports.ReportSlaSummaryDto's own doc comment):
// only TaskSla rows that have reached Completed count toward complianceRate; a still-Active/
// Warning/Overdue task is excluded from both the numerator and denominator, never counted as
// compliant just because it hasn't breached yet.
export interface ReportSlaSummary {
  active: number;
  warning: number;
  overdue: number;
  completed: number;
  completedWithinSla: number;
  completedBreachedSla: number;
  complianceRate: number | null;
}

export interface ReportSummary {
  process: ReportProcessSummary;
  processBreakdown: ReportProcessBreakdownItem[];
  taskSummary: ReportTaskSummary;
  approvalSummary: ReportApprovalSummary;
  slaSummary: ReportSlaSummary;
}

export type ReportProcessBreakdownSortBy = 'Total' | 'ProcessDefinitionName';

// One shared filter(+pagination+sort) shape reused across /summary, /details, and /export — the
// same filters always mean the same authorized+filtered scope everywhere (Part 31). Date range is
// on ProcessInstance.StartedAt, inclusive on both ends, UTC internally; this page may display
// local time but always sends ISO/UTC strings.
export interface ReportQuery {
  from?: string;
  to?: string;
  processDefinitionId?: string;
  status?: ProcessInstanceStatus;
  initiatorId?: string;
  departmentId?: string;
  slaStatus?: ProcessMonitoringSlaStatus;
  page?: number;
  pageSize?: number;
  sortBy?: ProcessMonitoringSortBy;
  sortDirection?: SortDirection;
  breakdownSortBy?: ReportProcessBreakdownSortBy;
}

// The Detail Table reuses ProcessMonitoringItemDto/ProcessMonitoringItem wholesale (Part 13/14) —
// this file never defines a second, parallel row shape for it.
export type ReportDetailItem = ProcessMonitoringItem;
