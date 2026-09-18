import { apiClient } from './apiClient';
import type { PagedResult } from '../types/process';
import type { AuditLogEntry, AuditLogQuery } from '../types/audit';

export async function queryAuditLogs(query: AuditLogQuery): Promise<PagedResult<AuditLogEntry>> {
  const response = await apiClient.get<PagedResult<AuditLogEntry>>('/api/audit-logs', { params: query });
  return response.data;
}
