import { apiClient } from './apiClient';
import type {
  AssignProcessOwnerRequest,
  CreateProcessDefinitionRequest,
  CreateProcessVersionRequest,
  PagedResult,
  ProcessDefinition,
  ProcessDefinitionQuery,
  ProcessLifecycleActionRequest,
  ProcessVersion,
  PublishProcessRequest,
  UpdateProcessDefinitionRequest,
  UpdateProcessVersionRequest,
  VersionComparisonResponse,
  WorkflowDefinition,
  WorkflowValidationResult,
} from '../types/process';

export async function listProcessDefinitions(query: ProcessDefinitionQuery): Promise<PagedResult<ProcessDefinition>> {
  const response = await apiClient.get<PagedResult<ProcessDefinition>>('/api/process-definitions', {
    params: query,
  });
  return response.data;
}

export async function getProcessDefinition(id: string): Promise<ProcessDefinition> {
  const response = await apiClient.get<ProcessDefinition>(`/api/process-definitions/${id}`);
  return response.data;
}

export async function createProcessDefinition(request: CreateProcessDefinitionRequest): Promise<ProcessDefinition> {
  const response = await apiClient.post<ProcessDefinition>('/api/process-definitions', request);
  return response.data;
}

export async function updateProcessDefinition(id: string, request: UpdateProcessDefinitionRequest): Promise<ProcessDefinition> {
  const response = await apiClient.put<ProcessDefinition>(`/api/process-definitions/${id}`, request);
  return response.data;
}

export async function listProcessVersions(processDefinitionId: string): Promise<ProcessVersion[]> {
  const response = await apiClient.get<ProcessVersion[]>(`/api/process-definitions/${processDefinitionId}/versions`);
  return response.data;
}

export async function createProcessVersion(
  processDefinitionId: string,
  request: CreateProcessVersionRequest,
): Promise<ProcessVersion> {
  const response = await apiClient.post<ProcessVersion>(`/api/process-definitions/${processDefinitionId}/versions`, request);
  return response.data;
}

export async function updateProcessVersion(
  processDefinitionId: string,
  versionId: string,
  request: UpdateProcessVersionRequest,
): Promise<ProcessVersion> {
  const response = await apiClient.put<ProcessVersion>(
    `/api/process-definitions/${processDefinitionId}/versions/${versionId}`,
    request,
  );
  return response.data;
}

export async function publishProcessVersion(processDefinitionId: string, request?: PublishProcessRequest): Promise<ProcessVersion> {
  const response = await apiClient.post<ProcessVersion>(`/api/process-definitions/${processDefinitionId}/publish`, request ?? {});
  return response.data;
}

export async function validateWorkflowDefinition(definition: WorkflowDefinition): Promise<WorkflowValidationResult> {
  const response = await apiClient.post<WorkflowValidationResult>('/api/process-definitions/validate', { definition });
  return response.data;
}

// --- Phase 8: Process Governance & Lifecycle ---

export async function suspendProcessDefinition(id: string, request: ProcessLifecycleActionRequest): Promise<ProcessDefinition> {
  const response = await apiClient.post<ProcessDefinition>(`/api/process-definitions/${id}/suspend`, request);
  return response.data;
}

export async function archiveProcessDefinition(id: string, request: ProcessLifecycleActionRequest): Promise<ProcessDefinition> {
  const response = await apiClient.post<ProcessDefinition>(`/api/process-definitions/${id}/archive`, request);
  return response.data;
}

export async function restoreProcessDefinition(id: string, request: ProcessLifecycleActionRequest): Promise<ProcessDefinition> {
  const response = await apiClient.post<ProcessDefinition>(`/api/process-definitions/${id}/restore`, request);
  return response.data;
}

export async function assignProcessOwner(id: string, request: AssignProcessOwnerRequest): Promise<ProcessDefinition> {
  const response = await apiClient.post<ProcessDefinition>(`/api/process-definitions/${id}/owner`, request);
  return response.data;
}

export async function compareProcessVersions(id: string, fromVersionId: string, toVersionId: string): Promise<VersionComparisonResponse> {
  const response = await apiClient.get<VersionComparisonResponse>(`/api/process-definitions/${id}/versions/compare`, {
    params: { fromVersionId, toVersionId },
  });
  return response.data;
}
