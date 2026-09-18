import { useQuery } from '@tanstack/react-query';
import { listRoles } from '../services/roleService';

export function useRoles() {
  const query = useQuery({ queryKey: ['roles'], queryFn: listRoles, staleTime: 5 * 60 * 1000 });
  return { roles: query.data ?? [], isLoading: query.isLoading, error: query.error };
}
