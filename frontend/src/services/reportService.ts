import axios from 'axios';
import { apiClient } from './apiClient';
import type { PagedResult } from '../types/process';
import type { ReportDetailItem, ReportQuery, ReportSummary } from '../types/report';

// currentUserId is never part of any of these requests — the server derives the authorized
// caller from the authenticated identity, exactly like Process Monitoring/Dashboard already do.
// Every ReportQuery filter (including initiatorId/departmentId) can only narrow that
// already-authorized scope, never broaden it.
export async function getReportSummary(query: ReportQuery): Promise<ReportSummary> {
  const response = await apiClient.get<ReportSummary>('/api/reports/summary', { params: query });
  return response.data;
}

export async function listReportDetails(query: ReportQuery): Promise<PagedResult<ReportDetailItem>> {
  const response = await apiClient.get<PagedResult<ReportDetailItem>>('/api/reports/details', { params: query });
  return response.data;
}

// No existing frontend Blob/download convention exists anywhere in this codebase yet (confirmed
// by inspection before writing this) — the only prior file-download precedent
// (AttachmentsController.Download) has never had a frontend caller. responseType: 'blob' + a
// short-lived object URL is the standard axios pattern; the caller is responsible for revoking
// the URL once the click has been dispatched (see ReportingPage's own export handler).
export async function exportReportCsv(query: ReportQuery): Promise<Blob> {
  try {
    const response = await apiClient.get('/api/reports/export', { params: query, responseType: 'blob' });
    return response.data as Blob;
  } catch (error) {
    // With responseType: 'blob', a JSON error body ({code, message, traceId}) arrives as a Blob
    // too, not parsed JSON — toApiError would otherwise treat it as an opaque body and fall back
    // to a generic message. Reparse it in place so 400 REPORT_EXPORT_TOO_LARGE/
    // REPORT_INVALID_DATE_RANGE surface with their real message, same as every other endpoint.
    if (axios.isAxiosError(error) && error.response?.data instanceof Blob && error.response.data.type.includes('json')) {
      try {
        error.response.data = JSON.parse(await error.response.data.text());
      } catch {
        // Not actually JSON — leave the Blob as-is; toApiError's status-code fallback handles it.
      }
    }
    throw error;
  }
}
