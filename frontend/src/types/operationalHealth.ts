// Mirrors BPM.Application.Administration.OperationalHealthDtos exactly. Every status is one of
// three honest values — this page must never render "NotInstrumented" as if it were "Healthy".
export type ComponentStatus = 'Healthy' | 'Unhealthy' | 'NotInstrumented';

export interface ComponentHealth {
  name: string;
  status: ComponentStatus;
  detail: string | null;
}

export interface NotificationHealth {
  pendingCount: number;
  processingCount: number;
  failedCount: number;
  sentLast24Hours: number;
}

export interface EmailConfiguration {
  enabled: boolean;
  provider: string;
}

export interface SlaSchedulerConfiguration {
  enabled: boolean;
  pollIntervalSeconds: number;
  batchSize: number;
}

export interface DashboardConfiguration {
  dueSoonHours: number;
}

export interface SafeConfiguration {
  email: EmailConfiguration;
  slaScheduler: SlaSchedulerConfiguration;
  dashboard: DashboardConfiguration;
}

export interface OperationalHealthResponse {
  api: ComponentHealth;
  database: ComponentHealth;
  objectStorage: ComponentHealth;
  cache: ComponentHealth;
  notificationDelivery: NotificationHealth;
  configuration: SafeConfiguration;
}
