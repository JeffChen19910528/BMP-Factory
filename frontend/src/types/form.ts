// Mirrors BPM.Application.Forms.FormDefinitionDtos and BPM.Domain.Forms.FormSchema — the current
// implementation is the source of truth, not Skill.md's original contract sketch. Reuses
// WorkflowValidationResult/-Error and PagedResult from types/process.ts rather than redefining
// them — the backend's validation-result and paging shapes are genuinely generic, not
// process-specific, even though they were first introduced there.
import type { WorkflowValidationError, WorkflowValidationResult } from './process';

export type { WorkflowValidationError, WorkflowValidationResult };

export type FormDefinitionStatus = 'Draft' | 'Published' | 'Suspended' | 'Archived';
export type FormVersionStatus = 'Draft' | 'Published';

export interface FormDefinition {
  id: string;
  key: string;
  name: string;
  description: string | null;
  category: string | null;
  status: FormDefinitionStatus;
  currentVersionId: string | null;
  createdAt: string;
  createdBy: string | null;
  updatedAt: string | null;
}

// Kept as a distinct alias from FormDefinition (not just `= FormDefinition`) so Phase 5.3.2's
// Process Designer Form-reference picker (which only ever needs id/key/name/status) keeps its own
// narrow, stable contract even if FormDefinition grows more fields later.
export type FormDefinitionSummary = FormDefinition;

export interface CreateFormDefinitionRequest {
  key: string;
  name: string;
  description?: string | null;
  category?: string | null;
}

export interface UpdateFormDefinitionRequest {
  name: string;
  description?: string | null;
  category?: string | null;
}

// --- Form schema (BPM.Domain.Forms.FormSchema) ---

export type FormFieldType =
  | 'Text'
  | 'Textarea'
  | 'Number'
  | 'Currency'
  | 'Date'
  | 'DateTime'
  | 'Select'
  | 'Radio'
  | 'Checkbox'
  | 'User'
  | 'Department'
  | 'File'
  | 'RichText'
  | 'Table'
  | 'Signature'
  | 'Formula'
  | 'Computed';

export interface FormFieldValidation {
  minLength?: number | null;
  maxLength?: number | null;
  pattern?: string | null;
  minValue?: number | null;
  maxValue?: number | null;
}

export type FormVisibilityOperator = 'Equals' | 'NotEquals';

export interface FormVisibilityCondition {
  field: string;
  operator: FormVisibilityOperator;
  value: string;
}

export interface FormFieldVisibility {
  when: FormVisibilityCondition;
}

export interface FormFieldOption {
  value: string;
  label: string;
}

export interface FormFieldDefinition {
  key: string;
  type: FormFieldType;
  label: string;
  description?: string | null;
  required?: boolean;
  defaultValue?: string | null;
  placeholder?: string | null;
  options?: FormFieldOption[] | null;
  validation?: FormFieldValidation | null;
  visibility?: FormFieldVisibility | null;
  readOnly?: boolean;
}

// --- Advanced Form Rules (Phase 5.4.2) — additive to the schema above, stored in the same
// FormSchema JSON as a top-level `rules` array. Mirrors BPM.Domain.Forms.FormRules exactly
// (property-for-property) since this is the one contract both the Designer and the backend
// FormRuleValidator/FormRuleEngine read. The legacy per-field `visibility` above is untouched and
// still works exactly as it did before this phase — this is a second, more general mechanism
// living alongside it, not a replacement. ---

export type FormRuleType = 'Visibility' | 'Enabled' | 'Required' | 'Calculated' | 'Unknown';

export type FormConditionOperator =
  | 'Equals'
  | 'NotEquals'
  | 'Contains'
  | 'NotContains'
  | 'GreaterThan'
  | 'GreaterThanOrEqual'
  | 'LessThan'
  | 'LessThanOrEqual'
  | 'IsEmpty'
  | 'IsNotEmpty';

export type FormLogicalOperator = 'And' | 'Or' | 'Not';

export interface FormFieldCondition {
  kind: 'field';
  field: string;
  operator: FormConditionOperator;
  value?: string | null;
}

export interface FormCompoundCondition {
  kind: 'compound';
  operator: FormLogicalOperator;
  conditions: FormCondition[];
}

export type FormCondition = FormFieldCondition | FormCompoundCondition;

export interface FormRule {
  id: string;
  target: string;
  type: FormRuleType;
  condition?: FormCondition | null;
  formula?: string | null;
  // Present only when type === 'Unknown' — the exact original JSON for a rule type this frontend
  // (and, at the time it was saved, the backend) doesn't understand yet. Never edited, never
  // dropped; serialized back out byte-for-byte on save (mirrors formSchemaModel.ts's `raw` field
  // for an unsupported FormFieldType).
  [key: string]: unknown;
}

export interface FormSchema {
  fields: FormFieldDefinition[];
  rules?: FormRule[] | null;
}

export interface FormVersion {
  id: string;
  formDefinitionId: string;
  versionNumber: number;
  status: FormVersionStatus;
  schema: FormSchema;
  createdAt: string;
  createdBy: string | null;
  publishedAt: string | null;
  publishedBy: string | null;
  // Base64 RowVersion — echo back as UpdateFormVersionRequest.expectedVersion to save; a stale
  // one is rejected with 409 FORM_VERSION_CONCURRENCY_CONFLICT (Phase 5.4.1, mirroring Phase
  // 5.3.2's ProcessVersion concurrency fix).
  rowVersion: string;
}

export interface CreateFormVersionRequest {
  schema: FormSchema;
}

export interface UpdateFormVersionRequest {
  schema: FormSchema;
  expectedVersion: string;
}
