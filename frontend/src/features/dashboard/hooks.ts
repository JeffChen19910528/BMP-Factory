import { useQuery } from '@tanstack/react-query';
import { getDashboard } from '../../services/dashboardService';

export const dashboardKeys = {
  all: ['dashboard'] as const,
};

// Phase 7.1 — Part 19: no WebSocket/SignalR, manual refresh plus a modest polling interval,
// mirroring NotificationBell's own 30s refetchInterval (Phase 6.1) rather than inventing a
// different cadence for a page that changes just as often.
export function useDashboard() {
  return useQuery({ queryKey: dashboardKeys.all, queryFn: getDashboard, refetchInterval: 30000 });
}
