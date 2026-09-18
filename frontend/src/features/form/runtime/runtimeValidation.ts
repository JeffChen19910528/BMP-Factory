import type { FormSchema, WorkflowValidationResult } from '../../../types/form';
import type { EvaluatedFormState } from '../designer/ruleEngine';

export interface FieldError {
  fieldKey: string;
  message: string;
}

// User-friendly client-side validation (Phase 5.4.3 §7) — required (including rule-derived
// conditional required, from the same `evaluateForm` state the renderer itself uses), basic
// type/format checks, and the existing FormFieldValidation constraints already defined by Phase 4
// (minLength/maxLength/pattern/minValue/maxValue). This is UX only: the backend re-validates
// everything itself at Save/Submit regardless of what this function decides (§12), so getting a
// check "wrong" here can only produce an unnecessary error message, never let bad data through.
export function validateClientSide(schema: FormSchema, state: EvaluatedFormState, values: Record<string, unknown>): FieldError[] {
  const errors: FieldError[] = [];

  for (const field of schema.fields) {
    const fieldState = state.fields[field.key];
    // A hidden field is still checked — the same "no visibility exemption" rule the backend
    // enforces (Phase 5.4.2 §12) — a rule-based required field that happens to be hidden must
    // still block submission, not be silently skipped by the client's own validation.
    const isCalculated = (schema.rules ?? []).some((r) => r.type === 'Calculated' && r.target === field.key);
    const value = isCalculated ? state.values[field.key] : values[field.key];
    const required = fieldState?.required ?? !!field.required;
    const isEmpty = value === undefined || value === null || value === '' || (Array.isArray(value) && value.length === 0);

    if (required && isEmpty) {
      errors.push({ fieldKey: field.key, message: `${field.label} is required.` });
      continue;
    }
    if (isEmpty) {
      continue;
    }

    if ((field.type === 'Number' || field.type === 'Currency') && typeof value !== 'number') {
      errors.push({ fieldKey: field.key, message: `${field.label} must be a number.` });
      continue;
    }
    if (field.type === 'Checkbox' && typeof value !== 'boolean') {
      errors.push({ fieldKey: field.key, message: `${field.label} must be checked or unchecked.` });
      continue;
    }

    const v = field.validation;
    if (v && typeof value === 'string') {
      if (v.minLength != null && value.length < v.minLength) {
        errors.push({ fieldKey: field.key, message: `${field.label} must be at least ${v.minLength} characters.` });
      } else if (v.maxLength != null && value.length > v.maxLength) {
        errors.push({ fieldKey: field.key, message: `${field.label} must be at most ${v.maxLength} characters.` });
      } else if (v.pattern) {
        try {
          if (!new RegExp(v.pattern).test(value)) {
            errors.push({ fieldKey: field.key, message: `${field.label} does not match the required format.` });
          }
        } catch {
          // An invalid pattern is a schema authoring problem, not a data problem — never crash
          // client validation over it; the backend's own FormSchemaValidator already rejects an
          // invalid regex at publish time, so a published form should never reach this branch.
        }
      }
    }
    if (v && typeof value === 'number') {
      if (v.minValue != null && value < v.minValue) {
        errors.push({ fieldKey: field.key, message: `${field.label} must be at least ${v.minValue}.` });
      } else if (v.maxValue != null && value > v.maxValue) {
        errors.push({ fieldKey: field.key, message: `${field.label} must be at most ${v.maxValue}.` });
      }
    }
  }

  return errors;
}

// Maps a backend WorkflowValidationResult (from a rejected Save/Submit) back onto the field that
// caused it — the exact same "every single-quoted token in the message checked against known
// field keys, first match wins, never fabricate a mapping" heuristic
// features/form/designer/ValidationPanel.tsx already uses for schema-validation errors, applied
// here to *data*-validation errors instead (FormDataValidator's own messages already always
// quote the offending field's key, e.g. "Field 'destination' is required."). An error that
// matches nothing resolves to the sentinel field key `__unmatched__` so the caller can render it
// in a generic list rather than silently drop it or guess.
export function resolveRuntimeErrors(result: WorkflowValidationResult | null, schema: FormSchema): FieldError[] {
  if (!result) return [];
  const keys = new Set(schema.fields.map((f) => f.key));

  return result.errors.map((error) => {
    for (const match of error.message.matchAll(/'([^']+)'/g)) {
      if (keys.has(match[1])) {
        return { fieldKey: match[1], message: error.message };
      }
    }
    return { fieldKey: '__unmatched__', message: error.message };
  });
}
