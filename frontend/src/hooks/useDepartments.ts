import { useQuery } from '@tanstack/react-query';
import { listDepartments } from '../services/departmentService';

export function useDepartments() {
  const query = useQuery({ queryKey: ['departments'], queryFn: listDepartments, staleTime: 5 * 60 * 1000 });
  return { departments: query.data ?? [], isLoading: query.isLoading, error: query.error };
}
