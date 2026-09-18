import { apiClient } from './apiClient';
import type { PagedResult } from '../types/process';
import type { ApprovalWorklistItem, ApprovalWorklistQuery, DelegateTaskRequest, Task, TransferTaskRequest } from '../types/task';

// Phase 12 — GET /api/tasks now returns a PagedResult<Task> envelope (previously a bare array),
// since the backend query is genuinely paginated rather than unbounded. Unwrapped to a flat array
// here so every existing consumer (useMyTasks, TasksPage.tsx) needs no change — pageSize matches
// the backend's own generous default (200), preserving today's "see everything realistic" UX with
// no dedicated pagination UI, while the underlying query is now bounded rather than unbounded.
export async function listMyTasks(): Promise<Task[]> {
  const response = await apiClient.get<PagedResult<Task>>('/api/tasks', { params: { page: 1, pageSize: 200 } });
  return response.data.items;
}

// Phase 5.5.1 — Approvals Worklist. currentUserId is never part of this request: the server
// derives the authorized caller from the authenticated identity (ICurrentUserService), not from
// anything the client sends.
export async function listApprovalWorklist(query: ApprovalWorklistQuery): Promise<PagedResult<ApprovalWorklistItem>> {
  const response = await apiClient.get<PagedResult<ApprovalWorklistItem>>('/api/tasks/approvals', { params: query });
  return response.data;
}

export async function getTask(id: string): Promise<Task> {
  const response = await apiClient.get<Task>(`/api/tasks/${id}`);
  return response.data;
}

// Plain UserTask completion (no form attached to the node) — a form-bound task is instead
// completed as a side effect of submitting its FormInstance (see formInstanceService.submitForm /
// FormEngine.SubmitAsync -> WorkflowTransitions), never by calling this directly.
export async function completeTask(id: string): Promise<Task> {
  const response = await apiClient.post<Task>(`/api/tasks/${id}/complete`);
  return response.data;
}

export async function approveTask(id: string): Promise<Task> {
  const response = await apiClient.post<Task>(`/api/tasks/${id}/approve`);
  return response.data;
}

export async function rejectTask(id: string): Promise<Task> {
  const response = await apiClient.post<Task>(`/api/tasks/${id}/reject`);
  return response.data;
}

export async function returnTask(id: string): Promise<Task> {
  const response = await apiClient.post<Task>(`/api/tasks/${id}/return`);
  return response.data;
}

// Phase 3's own doc comment: delegate is additive (both the original owner and the delegate may
// act afterward) and original-owner-only (a delegate cannot re-delegate).
export async function delegateTask(id: string, request: DelegateTaskRequest): Promise<Task> {
  const response = await apiClient.post<Task>(`/api/tasks/${id}/delegate`, request);
  return response.data;
}

// Transfer only ever moves the caller's own assignment slot — there is no "transfer someone
// else's slot" admin override (Phase 3's own design note).
export async function transferTask(id: string, request: TransferTaskRequest): Promise<Task> {
  const response = await apiClient.post<Task>(`/api/tasks/${id}/transfer`, request);
  return response.data;
}

// Administrator-only on the backend (Phase 3) — the frontend only ever shows this action to an
// Administrator as a UX nicety; the real enforcement is server-side regardless.
export async function addApprover(id: string, userId: string): Promise<Task> {
  const response = await apiClient.post<Task>(`/api/tasks/${id}/approvers`, { userId });
  return response.data;
}
