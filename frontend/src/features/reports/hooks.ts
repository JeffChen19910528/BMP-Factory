import { useQuery } from '@tanstack/react-query';
import { getReportSummary, listReportDetails } from '../../services/reportService';
import type { ReportQuery } from '../../types/report';

export const reportKeys = {
  all: ['reports'] as const,
  summary: (query: ReportQuery) => [...reportKeys.all, 'summary', query] as const,
  details: (query: ReportQuery) => [...reportKeys.all, 'details', query] as const,
};

// Query keys carry the full ReportQuery object (Part 33) — changing any filter, the date range,
// the page, or the sort automatically produces a different cache entry rather than reusing
// incompatible cached data for a different filter combination.
export function useReportSummary(query: ReportQuery) {
  return useQuery({
    queryKey: reportKeys.summary(query),
    queryFn: () => getReportSummary(query),
    placeholderData: (previousData) => previousData,
  });
}

export function useReportDetails(query: ReportQuery) {
  return useQuery({
    queryKey: reportKeys.details(query),
    queryFn: () => listReportDetails(query),
    placeholderData: (previousData) => previousData,
  });
}
