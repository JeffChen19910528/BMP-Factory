import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { addApprover, approveTask, completeTask, delegateTask, getTask, listApprovalWorklist, listMyTasks, rejectTask, returnTask, transferTask } from '../../services/taskService';
import type { ApprovalWorklistQuery, DelegateTaskRequest, TransferTaskRequest } from '../../types/task';

export const taskKeys = {
  all: ['tasks'] as const,
  list: () => [...taskKeys.all, 'list'] as const,
  detail: (id: string) => [...taskKeys.all, 'detail', id] as const,
};

// Phase 5.5.1 — a separate key namespace from taskKeys (not nested under it) since the worklist
// query has its own parameter shape (search/status/page/pageSize) — mirrors processKeys.list's
// query-in-the-key pattern from features/process/hooks.ts exactly.
export const approvalWorklistKeys = {
  all: ['approval-worklist'] as const,
  list: (query: ApprovalWorklistQuery) => [...approvalWorklistKeys.all, 'list', query] as const,
};

export function useMyTasks() {
  return useQuery({ queryKey: taskKeys.list(), queryFn: listMyTasks });
}

export function useTask(id: string | undefined) {
  return useQuery({ queryKey: taskKeys.detail(id ?? ''), queryFn: () => getTask(id!), enabled: !!id });
}

// placeholderData keeps the previous page's rows visible while the next page loads — same
// pattern useProcessDefinitions already established — rather than flashing a loading spinner on
// every pagination click.
export function useApprovalWorklist(query: ApprovalWorklistQuery) {
  return useQuery({
    queryKey: approvalWorklistKeys.list(query),
    queryFn: () => listApprovalWorklist(query),
    placeholderData: (previous) => previous,
  });
}

function useInvalidateTaskMutation<TVariables>(mutationFn: (id: string, variables: TVariables) => Promise<unknown>) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, variables }: { id: string; variables: TVariables }) => mutationFn(id, variables),
    onSuccess: (_data, { id }) => {
      queryClient.invalidateQueries({ queryKey: taskKeys.list() });
      queryClient.invalidateQueries({ queryKey: taskKeys.detail(id) });
      // §23: a just-approved/rejected/etc. task must never keep showing in the worklist under
      // its old status — invalidate every cached worklist page/filter combination, not just the
      // one the user happened to come from.
      queryClient.invalidateQueries({ queryKey: approvalWorklistKeys.all });
    },
  });
}

export function useCompleteTask() {
  return useInvalidateTaskMutation<void>((id) => completeTask(id));
}

export function useApproveTask() {
  return useInvalidateTaskMutation<void>((id) => approveTask(id));
}

export function useRejectTask() {
  return useInvalidateTaskMutation<void>((id) => rejectTask(id));
}

export function useReturnTask() {
  return useInvalidateTaskMutation<void>((id) => returnTask(id));
}

export function useDelegateTask() {
  return useInvalidateTaskMutation<DelegateTaskRequest>((id, request) => delegateTask(id, request));
}

export function useTransferTask() {
  return useInvalidateTaskMutation<TransferTaskRequest>((id, request) => transferTask(id, request));
}

export function useAddApprover() {
  return useInvalidateTaskMutation<string>((id, userId) => addApprover(id, userId));
}
