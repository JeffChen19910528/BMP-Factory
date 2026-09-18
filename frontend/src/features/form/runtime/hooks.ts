import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { deleteAttachment, getFormInstance, getFormInstanceData, listAttachments, listFormInstancesByProcessInstance, saveFormInstanceData, submitFormInstance, uploadAttachment } from '../../../services/formInstanceService';
import { listFormVersions } from '../../../services/formDefinitionService';
import type { UpdateFormDataRequest } from '../../../types/formInstance';

export const formInstanceKeys = {
  all: ['form-instances'] as const,
  byProcess: (processInstanceId: string) => [...formInstanceKeys.all, 'by-process', processInstanceId] as const,
  detail: (id: string) => [...formInstanceKeys.all, 'detail', id] as const,
  data: (id: string) => [...formInstanceKeys.all, 'data', id] as const,
  attachments: (id: string) => [...formInstanceKeys.all, 'attachments', id] as const,
};

export function useFormInstancesByProcess(processInstanceId: string | undefined) {
  return useQuery({
    queryKey: formInstanceKeys.byProcess(processInstanceId ?? ''),
    queryFn: () => listFormInstancesByProcessInstance(processInstanceId!),
    enabled: !!processInstanceId,
  });
}

export function useFormInstance(id: string | undefined) {
  return useQuery({ queryKey: formInstanceKeys.detail(id ?? ''), queryFn: () => getFormInstance(id!), enabled: !!id });
}

export function useFormInstanceData(id: string | undefined) {
  return useQuery({ queryKey: formInstanceKeys.data(id ?? ''), queryFn: () => getFormInstanceData(id!), enabled: !!id });
}

// The published FormVersion a FormInstance is pinned to — reuses the exact same
// listFormVersions(formDefinitionId) the Form Designer's Form Detail page already calls; there is
// no dedicated "get one version" endpoint, so this finds it the same way FormDetailPage does.
export function useFormInstanceSchema(formDefinitionId: string | undefined, formVersionId: string | undefined) {
  const query = useQuery({
    queryKey: ['form-definitions', formDefinitionId, 'versions'],
    queryFn: () => listFormVersions(formDefinitionId!),
    enabled: !!formDefinitionId,
  });
  const version = query.data?.find((v) => v.id === formVersionId);
  return { version, isLoading: query.isLoading, error: query.error };
}

export function useSaveFormInstanceData(formInstanceId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: UpdateFormDataRequest) => saveFormInstanceData(formInstanceId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: formInstanceKeys.data(formInstanceId) });
    },
  });
}

export function useSubmitFormInstance(formInstanceId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => submitFormInstance(formInstanceId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: formInstanceKeys.detail(formInstanceId) });
      queryClient.invalidateQueries({ queryKey: formInstanceKeys.data(formInstanceId) });
    },
  });
}

export function useAttachments(formInstanceId: string | undefined) {
  return useQuery({
    queryKey: formInstanceKeys.attachments(formInstanceId ?? ''),
    queryFn: () => listAttachments(formInstanceId!),
    enabled: !!formInstanceId,
  });
}

export function useUploadAttachment(formInstanceId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (file: File) => uploadAttachment(formInstanceId, file),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: formInstanceKeys.attachments(formInstanceId) });
    },
  });
}

export function useDeleteAttachment(formInstanceId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (attachmentId: string) => deleteAttachment(attachmentId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: formInstanceKeys.attachments(formInstanceId) });
    },
  });
}
