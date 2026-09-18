import { apiClient } from './apiClient';
import type { PagedResult } from '../types/process';
import type { ProcessInstanceDetail, ProcessMonitoringItem, ProcessMonitoringQuery } from '../types/processMonitoring';

// currentUserId is never part of this request: the server derives the authorized caller from the
// authenticated identity (ICurrentUserService), and every filter here (including initiatorId) can
// only narrow that already-authorized scope, never broaden it — see
// ProcessMonitoringQueryService's own comment on the backend.
export async function listProcessMonitoring(query: ProcessMonitoringQuery): Promise<PagedResult<ProcessMonitoringItem>> {
  const response = await apiClient.get<PagedResult<ProcessMonitoringItem>>('/api/process-monitoring', { params: query });
  return response.data;
}

// Phase 7.2.2 — a 403/404 from the backend surfaces as a normal thrown axios error here (via
// apiClient's own interceptor) — the caller (ProcessInstanceDetailPage) renders it through the
// existing ApiErrorAlert, never silently redirected or swallowed.
export async function getProcessInstanceDetail(processInstanceId: string): Promise<ProcessInstanceDetail> {
  const response = await apiClient.get<ProcessInstanceDetail>(`/api/process-monitoring/${processInstanceId}`);
  return response.data;
}
