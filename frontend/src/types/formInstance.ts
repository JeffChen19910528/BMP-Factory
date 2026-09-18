// Mirrors BPM.Application.Forms.FormInstanceDtos — FormInstance/FormData are the existing Phase 4
// runtime persistence model; the Form Runtime UX (Phase 5.4.3) reuses it as-is, never introducing
// a second persistence mechanism.

export type FormInstanceStatus = 'Draft' | 'Submitted' | 'Locked' | 'Cancelled';

export interface FormInstance {
  id: string;
  formDefinitionId: string;
  formVersionId: string;
  processInstanceId: string | null;
  taskInstanceId: string | null;
  createdByUserId: string;
  status: FormInstanceStatus;
}

export interface CreateFormInstanceRequest {
  formDefinitionKey: string;
  processInstanceId?: string | null;
}

// `version` is the FormData row's RowVersion, base64-encoded — echo back as
// UpdateFormDataRequest.expectedVersion to save; a stale one is rejected with
// 409 FORM_CONCURRENCY_CONFLICT (Phase 4 §26), same pattern Phase 5.3.2/5.4.1 use for
// ProcessVersion/FormVersion.
export interface FormDataDto {
  formInstanceId: string;
  data: Record<string, unknown>;
  version: string;
}

export interface UpdateFormDataRequest {
  data: Record<string, unknown>;
  expectedVersion: string;
}

export interface Attachment {
  id: string;
  formInstanceId: string;
  fileName: string;
  contentType: string;
  size: number;
  hash: string;
  uploadedByUserId: string;
  uploadedAt: string;
}
