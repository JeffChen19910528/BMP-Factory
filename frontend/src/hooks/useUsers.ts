import { useQuery } from '@tanstack/react-query';
import { listUsers } from '../services/userService';

// Backend DTOs expose actor identity as a raw user id (see AuditLogDto, ProcessDefinitionDto's
// CreatedBy) rather than a resolved display name — consistent with the existing convention, name
// resolution happens client-side via GET /api/users, which every authenticated user can call.
export function useUsersById() {
  const query = useQuery({
    queryKey: ['users'],
    queryFn: listUsers,
    staleTime: 5 * 60 * 1000,
  });

  const byId = new Map((query.data ?? []).map((u) => [u.id, u.displayName]));

  return { byId, users: query.data ?? [], isLoading: query.isLoading };
}
