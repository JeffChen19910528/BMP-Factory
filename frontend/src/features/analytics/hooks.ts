import { useQuery } from '@tanstack/react-query';
import { getAnalyticsOverview } from '../../services/analyticsService';
import type { AnalyticsQuery } from '../../types/analytics';

export const analyticsKeys = {
  all: ['analytics'] as const,
  overview: (query: AnalyticsQuery) => [...analyticsKeys.all, 'overview', query] as const,
};

export function useAnalyticsOverview(query: AnalyticsQuery) {
  return useQuery({
    queryKey: analyticsKeys.overview(query),
    queryFn: () => getAnalyticsOverview(query),
    placeholderData: (previousData) => previousData,
  });
}
