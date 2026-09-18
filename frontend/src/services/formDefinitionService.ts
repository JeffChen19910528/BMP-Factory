import { apiClient } from './apiClient';
import type {
  CreateFormDefinitionRequest,
  CreateFormVersionRequest,
  FormDefinition,
  FormSchema,
  FormVersion,
  UpdateFormDefinitionRequest,
  UpdateFormVersionRequest,
  WorkflowValidationResult,
} from '../types/form';

export async function listFormDefinitions(): Promise<FormDefinition[]> {
  const response = await apiClient.get<FormDefinition[]>('/api/form-definitions');
  return response.data;
}

export async function getFormDefinition(id: string): Promise<FormDefinition> {
  const response = await apiClient.get<FormDefinition>(`/api/form-definitions/${id}`);
  return response.data;
}

export async function createFormDefinition(request: CreateFormDefinitionRequest): Promise<FormDefinition> {
  const response = await apiClient.post<FormDefinition>('/api/form-definitions', request);
  return response.data;
}

export async function updateFormDefinition(id: string, request: UpdateFormDefinitionRequest): Promise<FormDefinition> {
  const response = await apiClient.put<FormDefinition>(`/api/form-definitions/${id}`, request);
  return response.data;
}

export async function listFormVersions(formDefinitionId: string): Promise<FormVersion[]> {
  const response = await apiClient.get<FormVersion[]>(`/api/form-definitions/${formDefinitionId}/versions`);
  return response.data;
}

export async function createFormVersion(formDefinitionId: string, request: CreateFormVersionRequest): Promise<FormVersion> {
  const response = await apiClient.post<FormVersion>(`/api/form-definitions/${formDefinitionId}/versions`, request);
  return response.data;
}

export async function updateFormVersion(
  formDefinitionId: string,
  versionId: string,
  request: UpdateFormVersionRequest,
): Promise<FormVersion> {
  const response = await apiClient.put<FormVersion>(`/api/form-definitions/${formDefinitionId}/versions/${versionId}`, request);
  return response.data;
}

export async function publishFormVersion(formDefinitionId: string): Promise<FormVersion> {
  const response = await apiClient.post<FormVersion>(`/api/form-definitions/${formDefinitionId}/publish`);
  return response.data;
}

export async function validateFormSchema(schema: FormSchema): Promise<WorkflowValidationResult> {
  const response = await apiClient.post<WorkflowValidationResult>('/api/form-definitions/validate', { schema });
  return response.data;
}
