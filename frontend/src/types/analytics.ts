import type { ProcessInstanceStatus } from './process';

// Phase 7.4 — Analytics. Mirrors BPM.Application.Analytics.AnalyticsDtos exactly. This page never
// computes a trend/duration/compliance figure itself — every number comes straight from
// GET /api/analytics/overview.

export type AnalyticsGranularity = 'Day' | 'Week' | 'Month';

export interface AnalyticsQuery {
  from?: string;
  to?: string;
  processDefinitionId?: string;
  status?: ProcessInstanceStatus;
  initiatorId?: string;
  departmentId?: string;
  granularity?: AnalyticsGranularity;
}

// SampleCount always accompanies Average/Min/Max (Part 17) so a caller can judge reliability.
// Median is deliberately not implemented — see AnalyticsQueryService's own comment (no
// translatable PostgreSQL percentile function in the installed EF provider, and hand-written raw
// SQL for one statistic was judged not worth the fragility).
export interface DurationStats {
  sampleCount: number;
  averageHours: number | null;
  minHours: number | null;
  maxHours: number | null;
}

export interface VolumeTrendPoint {
  bucketStart: string;
  started: number;
  completed: number;
  rejected: number;
}

export interface TaskThroughputPoint {
  bucketStart: string;
  created: number;
  completed: number;
}

export interface NodeAnalyticsItem {
  nodeId: string;
  nodeName: string;
  executions: number;
  completed: number;
  duration: DurationStats;
  overdueCount: number;
}

export interface SlaTrendPoint {
  bucketStart: string;
  completedSlaTasks: number;
  compliantCount: number;
  breachedCount: number;
  complianceRate: number | null;
}

export interface ProcessComparisonItem {
  processDefinitionId: string;
  processDefinitionKey: string;
  processDefinitionName: string;
  total: number;
  completed: number;
  rejected: number;
  duration: DurationStats;
  slaComplianceRate: number | null;
  overdueCount: number;
}

export interface AnalyticsOverview {
  totalProcesses: number;
  runningProcesses: number;
  completedProcesses: number;
  rejectedProcesses: number;
  volumeTrend: VolumeTrendPoint[];
  processDuration: DurationStats;
  taskThroughputTrend: TaskThroughputPoint[];
  taskDuration: DurationStats;
  nodeAnalytics: NodeAnalyticsItem[];
  slaTrend: SlaTrendPoint[];
  processComparison: ProcessComparisonItem[];
}
