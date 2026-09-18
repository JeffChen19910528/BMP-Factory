import { useQuery } from '@tanstack/react-query';
import { getProcessInstanceDetail, listProcessMonitoring } from '../../services/processMonitoringService';
import type { ProcessMonitoringQuery } from '../../types/processMonitoring';

export const processMonitoringKeys = {
  all: ['process-monitoring'] as const,
  list: (query: ProcessMonitoringQuery) => [...processMonitoringKeys.all, 'list', query] as const,
  detail: (id: string) => [...processMonitoringKeys.all, 'detail', id] as const,
};

// placeholderData keeps the previous page's rows visible while the next page/filter loads —
// mirrors useApprovalWorklist/useProcessDefinitions' own established pattern rather than flashing
// a loading spinner on every filter change or pagination click.
export function useProcessMonitoring(query: ProcessMonitoringQuery) {
  return useQuery({
    queryKey: processMonitoringKeys.list(query),
    queryFn: () => listProcessMonitoring(query),
    placeholderData: (previousData) => previousData,
  });
}

export function useProcessInstanceDetail(processInstanceId: string | undefined) {
  return useQuery({
    queryKey: processMonitoringKeys.detail(processInstanceId ?? ''),
    queryFn: () => getProcessInstanceDetail(processInstanceId!),
    enabled: !!processInstanceId,
    retry: false, // a 403/404 should surface immediately, not retry against an authorization wall.
  });
}
