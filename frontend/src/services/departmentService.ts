import { apiClient } from './apiClient';
import type { CreateDepartmentRequest, Department, UpdateDepartmentRequest } from '../types/department';

export async function listDepartments(): Promise<Department[]> {
  const response = await apiClient.get<Department[]>('/api/departments');
  return response.data;
}

export async function createDepartment(request: CreateDepartmentRequest): Promise<Department> {
  const response = await apiClient.post<Department>('/api/departments', request);
  return response.data;
}

export async function updateDepartment(id: string, request: UpdateDepartmentRequest): Promise<Department> {
  const response = await apiClient.put<Department>(`/api/departments/${id}`, request);
  return response.data;
}
