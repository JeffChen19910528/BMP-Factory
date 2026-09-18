import { apiClient } from './apiClient';
import type { Attachment, CreateFormInstanceRequest, FormDataDto, FormInstance, UpdateFormDataRequest } from '../types/formInstance';

export async function getFormInstance(id: string): Promise<FormInstance> {
  const response = await apiClient.get<FormInstance>(`/api/form-instances/${id}`);
  return response.data;
}

// Used to resolve a Task's bound FormInstance (Phase 5.4.3): WorkflowTransitions auto-creates the
// FormInstance alongside the TaskInstance when the UserTask node has a Form reference, so by the
// time a task appears in "My Tasks" the instance already exists — the caller finds it by filtering
// this list for `taskInstanceId === task.id` rather than this service creating one itself.
export async function listFormInstancesByProcessInstance(processInstanceId: string): Promise<FormInstance[]> {
  const response = await apiClient.get<FormInstance[]>('/api/form-instances', { params: { processInstanceId } });
  return response.data;
}

// Standalone (non-task) form instance creation — not used by the Task Runtime flow above, kept
// for a future "fill out a form on its own" entry point that already has a published Form to
// reference.
export async function createFormInstance(request: CreateFormInstanceRequest): Promise<FormInstance> {
  const response = await apiClient.post<FormInstance>('/api/form-instances', request);
  return response.data;
}

export async function getFormInstanceData(id: string): Promise<FormDataDto> {
  const response = await apiClient.get<FormDataDto>(`/api/form-instances/${id}/data`);
  return response.data;
}

export async function saveFormInstanceData(id: string, request: UpdateFormDataRequest): Promise<FormDataDto> {
  const response = await apiClient.put<FormDataDto>(`/api/form-instances/${id}/data`, request);
  return response.data;
}

export async function submitFormInstance(id: string): Promise<FormInstance> {
  const response = await apiClient.post<FormInstance>(`/api/form-instances/${id}/submit`);
  return response.data;
}

export async function cancelFormInstance(id: string): Promise<FormInstance> {
  const response = await apiClient.post<FormInstance>(`/api/form-instances/${id}/cancel`);
  return response.data;
}

export async function listAttachments(formInstanceId: string): Promise<Attachment[]> {
  const response = await apiClient.get<Attachment[]>(`/api/form-instances/${formInstanceId}/attachments`);
  return response.data;
}

export async function uploadAttachment(formInstanceId: string, file: File): Promise<Attachment> {
  const formData = new FormData();
  formData.append('file', file);
  const response = await apiClient.post<Attachment>(`/api/form-instances/${formInstanceId}/attachments`, formData, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return response.data;
}

export function attachmentDownloadUrl(attachmentId: string): string {
  return `/api/attachments/${attachmentId}`;
}

export async function deleteAttachment(attachmentId: string): Promise<void> {
  await apiClient.delete(`/api/attachments/${attachmentId}`);
}
