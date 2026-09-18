import { apiClient } from './apiClient';
import type { AnalyticsOverview, AnalyticsQuery } from '../types/analytics';

// currentUserId is never part of this request — the server derives the authorized caller from
// the authenticated identity, exactly like Reporting/Process Monitoring/Dashboard already do.
export async function getAnalyticsOverview(query: AnalyticsQuery): Promise<AnalyticsOverview> {
  const response = await apiClient.get<AnalyticsOverview>('/api/analytics/overview', { params: query });
  return response.data;
}
