// Phase 7.1 — Dashboard Foundation. Mirrors BPM.Application.Dashboard.DashboardDtos exactly — the
// backend is the sole authority for every number here; this file only shapes what the API already
// returns, it never computes anything (no client-side Overdue/DueSoon/status derivation anywhere
// in this feature).

export interface DashboardTaskSummary {
  total: number;
  overdue: number;
  dueSoon: number;
}

export interface DashboardApprovalSummary {
  pending: number;
}

export interface DashboardSlaSummary {
  active: number;
  warning: number;
  overdue: number;
  completed: number;
}

export interface DashboardProcessSummary {
  running: number;
  completed: number;
  rejected: number;
}

export interface DashboardActivityItem {
  timestamp: string;
  action: string;
  description: string;
  processInstanceId: string | null;
}

export interface DashboardResponse {
  myTasks: DashboardTaskSummary;
  pendingApprovals: DashboardApprovalSummary;
  sla: DashboardSlaSummary;
  processOverview: DashboardProcessSummary;
  recentActivity: DashboardActivityItem[];
}
