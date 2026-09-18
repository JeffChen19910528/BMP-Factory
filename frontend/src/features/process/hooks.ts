import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  archiveProcessDefinition,
  assignProcessOwner,
  compareProcessVersions,
  createProcessDefinition,
  createProcessVersion,
  getProcessDefinition,
  listProcessDefinitions,
  listProcessVersions,
  publishProcessVersion,
  restoreProcessDefinition,
  suspendProcessDefinition,
  updateProcessDefinition,
  updateProcessVersion,
} from '../../services/processService';
import type { ProcessDefinitionQuery, PublishProcessRequest } from '../../types/process';

// Centralizes TanStack Query keys/hooks for Process Management so pages call these instead of
// the service functions directly (frontend spec §13: "Do not duplicate HTTP logic inside React
// pages").
export const processKeys = {
  all: ['process-definitions'] as const,
  list: (query: ProcessDefinitionQuery) => [...processKeys.all, 'list', query] as const,
  detail: (id: string) => [...processKeys.all, 'detail', id] as const,
  versions: (id: string) => [...processKeys.all, 'detail', id, 'versions'] as const,
};

export function useProcessDefinitions(query: ProcessDefinitionQuery) {
  return useQuery({
    queryKey: processKeys.list(query),
    queryFn: () => listProcessDefinitions(query),
    placeholderData: (previous) => previous,
  });
}

export function useProcessDefinition(id: string | undefined) {
  return useQuery({
    queryKey: processKeys.detail(id ?? ''),
    queryFn: () => getProcessDefinition(id!),
    enabled: !!id,
  });
}

export function useProcessVersions(id: string | undefined) {
  return useQuery({
    queryKey: processKeys.versions(id ?? ''),
    queryFn: () => listProcessVersions(id!),
    enabled: !!id,
  });
}

export function useCreateProcessDefinition() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: createProcessDefinition,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: processKeys.all });
    },
  });
}

export function useUpdateProcessDefinition(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: Parameters<typeof updateProcessDefinition>[1]) => updateProcessDefinition(id, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: processKeys.detail(id) });
      queryClient.invalidateQueries({ queryKey: processKeys.all });
    },
  });
}

export function useCreateProcessVersion(processDefinitionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: Parameters<typeof createProcessVersion>[1]) => createProcessVersion(processDefinitionId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: processKeys.versions(processDefinitionId) });
      queryClient.invalidateQueries({ queryKey: processKeys.detail(processDefinitionId) });
    },
  });
}

export function useUpdateProcessVersion(processDefinitionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ versionId, request }: { versionId: string; request: Parameters<typeof updateProcessVersion>[2] }) =>
      updateProcessVersion(processDefinitionId, versionId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: processKeys.versions(processDefinitionId) });
    },
  });
}

export function usePublishProcessVersion(processDefinitionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request?: PublishProcessRequest) => publishProcessVersion(processDefinitionId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: processKeys.versions(processDefinitionId) });
      queryClient.invalidateQueries({ queryKey: processKeys.detail(processDefinitionId) });
      queryClient.invalidateQueries({ queryKey: processKeys.all });
    },
  });
}

// --- Phase 8: Process Governance & Lifecycle ---

export function useSuspendProcessDefinition(processDefinitionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: Parameters<typeof suspendProcessDefinition>[1]) => suspendProcessDefinition(processDefinitionId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: processKeys.detail(processDefinitionId) });
      queryClient.invalidateQueries({ queryKey: processKeys.all });
    },
  });
}

export function useArchiveProcessDefinition(processDefinitionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: Parameters<typeof archiveProcessDefinition>[1]) => archiveProcessDefinition(processDefinitionId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: processKeys.detail(processDefinitionId) });
      queryClient.invalidateQueries({ queryKey: processKeys.all });
    },
  });
}

export function useRestoreProcessDefinition(processDefinitionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: Parameters<typeof restoreProcessDefinition>[1]) => restoreProcessDefinition(processDefinitionId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: processKeys.detail(processDefinitionId) });
      queryClient.invalidateQueries({ queryKey: processKeys.all });
    },
  });
}

export function useAssignProcessOwner(processDefinitionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: Parameters<typeof assignProcessOwner>[1]) => assignProcessOwner(processDefinitionId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: processKeys.detail(processDefinitionId) });
      queryClient.invalidateQueries({ queryKey: processKeys.all });
    },
  });
}

export function useCompareProcessVersions(processDefinitionId: string, fromVersionId: string | undefined, toVersionId: string | undefined) {
  return useQuery({
    queryKey: [...processKeys.versions(processDefinitionId), 'compare', fromVersionId, toVersionId],
    queryFn: () => compareProcessVersions(processDefinitionId, fromVersionId!, toVersionId!),
    enabled: !!fromVersionId && !!toVersionId,
  });
}
