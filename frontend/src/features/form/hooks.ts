import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  createFormDefinition,
  createFormVersion,
  getFormDefinition,
  listFormDefinitions,
  listFormVersions,
  publishFormVersion,
  updateFormDefinition,
  updateFormVersion,
} from '../../services/formDefinitionService';

// Centralizes TanStack Query keys/hooks for Form Management, mirroring
// features/process/hooks.ts exactly — pages call these instead of the service functions
// directly (no HTTP logic inside a page component).
export const formKeys = {
  all: ['form-definitions'] as const,
  list: () => [...formKeys.all, 'list'] as const,
  detail: (id: string) => [...formKeys.all, 'detail', id] as const,
  versions: (id: string) => [...formKeys.all, 'detail', id, 'versions'] as const,
};

export function useFormDefinitions() {
  return useQuery({ queryKey: formKeys.list(), queryFn: listFormDefinitions });
}

export function useFormDefinition(id: string | undefined) {
  return useQuery({
    queryKey: formKeys.detail(id ?? ''),
    queryFn: () => getFormDefinition(id!),
    enabled: !!id,
  });
}

export function useFormVersions(id: string | undefined) {
  return useQuery({
    queryKey: formKeys.versions(id ?? ''),
    queryFn: () => listFormVersions(id!),
    enabled: !!id,
  });
}

export function useCreateFormDefinition() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: createFormDefinition,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: formKeys.all });
    },
  });
}

export function useUpdateFormDefinition(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: Parameters<typeof updateFormDefinition>[1]) => updateFormDefinition(id, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: formKeys.detail(id) });
      queryClient.invalidateQueries({ queryKey: formKeys.all });
    },
  });
}

export function useCreateFormVersion(formDefinitionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: Parameters<typeof createFormVersion>[1]) => createFormVersion(formDefinitionId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: formKeys.versions(formDefinitionId) });
      queryClient.invalidateQueries({ queryKey: formKeys.detail(formDefinitionId) });
    },
  });
}

export function useUpdateFormVersion(formDefinitionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ versionId, request }: { versionId: string; request: Parameters<typeof updateFormVersion>[2] }) =>
      updateFormVersion(formDefinitionId, versionId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: formKeys.versions(formDefinitionId) });
    },
  });
}

export function usePublishFormVersion(formDefinitionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => publishFormVersion(formDefinitionId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: formKeys.versions(formDefinitionId) });
      queryClient.invalidateQueries({ queryKey: formKeys.detail(formDefinitionId) });
      queryClient.invalidateQueries({ queryKey: formKeys.all });
    },
  });
}
