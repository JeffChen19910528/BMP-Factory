import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Card, Space, Tag, Typography } from 'antd';
import type { FormSchema, WorkflowValidationResult } from '../../../types/form';
import { useTranslation } from '../../../i18n/LanguageContext';
import { evaluateForm } from '../designer/ruleEngine';
import { RuntimeField } from './RuntimeField';
import { resolveRuntimeErrors, validateClientSide, type FieldError } from './runtimeValidation';

interface FormRuntimeProps {
  schema: FormSchema;
  formName?: string;
  formInstanceId: string;
  // The FormData last saved on the server (or {} for a brand-new instance) — the runtime's own
  // editable state is seeded from this once (see the mount-only baseline note below) and never
  // silently reset back to it, so a user's in-progress edits are never clobbered by a background
  // refetch.
  initialData: Record<string, unknown>;
  readOnly: boolean;
  onChange: (data: Record<string, unknown>, dirty: boolean) => void;
  onSaveDraft: () => void;
  isSaving?: boolean;
  onSubmit: () => void;
  isSubmitting?: boolean;
  // Structured field errors from the last backend rejection (Phase 5.4.3 §8) — mapped back onto
  // the field that caused them via the same single-quoted-token heuristic the Designer's
  // ValidationPanel already uses for schema validation errors, applied here to *data* validation
  // errors instead.
  backendResult?: WorkflowValidationResult | null;
}

// The one schema-driven form renderer (Phase 5.4.3 §2/§31): fields come entirely from the
// existing FormSchema — no hard-coded per-form layout, no second schema. Rule evaluation reuses
// the exact `evaluateForm` from Phase 5.4.2's ruleEngine.ts (the same implementation the Designer's
// own Preview uses) rather than a second "runtime rule engine" (§30) — every keystroke
// re-evaluates the whole schema against the values entered so far, exactly like Preview does,
// except this time the data is real and actually gets saved/submitted. This evaluation is UX only
// — the backend re-evaluates everything itself at Save/Submit regardless (§12).
export function FormRuntime({
  schema,
  formName,
  formInstanceId,
  initialData,
  readOnly,
  onChange,
  onSaveDraft,
  isSaving,
  onSubmit,
  isSubmitting,
  backendResult,
}: FormRuntimeProps) {
  const { t } = useTranslation();
  // Computed once at mount, same reasoning as the Form/Process Designers' identical pattern: the
  // parent echoes every onChange back into whatever prop it passes as `initialData`, so tracking
  // it reactively would make the dirty baseline chase live state and `dirty` would always read
  // false after the first edit. A fresh baseline only happens on an explicit remount (the parent
  // bumps a `key`, e.g. after Reload), never from this prop changing on its own.
  const [savedJson] = useState(() => JSON.stringify(initialData));
  const [values, setValues] = useState<Record<string, unknown>>(initialData);
  const [clientErrors, setClientErrors] = useState<FieldError[] | null>(null);

  const state = useMemo(() => evaluateForm(schema, values), [schema, values]);
  const currentJson = useMemo(() => JSON.stringify(values), [values]);
  const dirty = currentJson !== savedJson;

  useEffect(() => {
    onChange(values, dirty);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentJson, dirty]);

  const backendErrors = useMemo(() => resolveRuntimeErrors(backendResult ?? null, schema), [backendResult, schema]);
  const errorsByField = new Map<string, string>();
  for (const e of clientErrors ?? []) errorsByField.set(e.fieldKey, e.message);
  for (const e of backendErrors) errorsByField.set(e.fieldKey, e.message);

  function handleFieldChange(key: string, value: unknown) {
    setValues((prev) => ({ ...prev, [key]: value }));
    setClientErrors(null);
  }

  function handleSubmitClick() {
    const errors = validateClientSide(schema, state, values);
    if (errors.length > 0) {
      setClientErrors(errors);
      return;
    }
    setClientErrors(null);
    onSubmit();
  }

  const unmatchedBackendErrors = backendErrors.filter((e) => e.fieldKey === '__unmatched__');

  return (
    <Card
      title={
        <Space>
          <span>{formName ?? t('forms.formFallbackTitle')}</span>
          {readOnly && <Tag color="green">{t('forms.submittedReadOnlyTag')}</Tag>}
        </Space>
      }
    >
      {unmatchedBackendErrors.length > 0 && (
        <Alert
          style={{ marginBottom: 16 }}
          type="error"
          showIcon
          message={t('forms.submissionFailed')}
          description={
            <ul style={{ margin: 0, paddingInlineStart: 20 }}>
              {unmatchedBackendErrors.map((e, i) => (
                <li key={i}>{e.message}</li>
              ))}
            </ul>
          }
        />
      )}

      <Space direction="vertical" style={{ width: '100%' }} size="large">
        {schema.fields.map((field) => {
          const fieldState = state.fields[field.key];
          if (fieldState && !fieldState.visible) return null;
          const isCalculated = (schema.rules ?? []).some((r) => r.type === 'Calculated' && r.target === field.key);
          const fieldValue = isCalculated ? state.values[field.key] : values[field.key];
          const error = errorsByField.get(field.key);

          return (
            <div key={field.key}>
              {field.type !== 'Checkbox' && (
                <div style={{ marginBottom: 4 }}>
                  <Typography.Text strong>{field.label}</Typography.Text>
                  {fieldState?.required && <Typography.Text type="danger"> *</Typography.Text>}
                </div>
              )}
              <RuntimeField
                field={field}
                value={fieldValue}
                disabled={readOnly || (fieldState ? !fieldState.enabled : false) || isCalculated}
                onChange={(v) => handleFieldChange(field.key, v)}
                formInstanceId={formInstanceId}
              />
              {field.description && (
                <div style={{ fontSize: 12, color: 'rgba(0,0,0,0.45)', marginTop: 4 }}>{field.description}</div>
              )}
              {state.calculationErrors[field.key] && (
                <Typography.Text type="danger" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                  ❌ {t('forms.cannotCalculateThisValue')} ({state.calculationErrors[field.key]}).
                </Typography.Text>
              )}
              {error && (
                <Typography.Text type="danger" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
                  ❌ {error}
                </Typography.Text>
              )}
            </div>
          );
        })}
      </Space>

      {!readOnly && (
        <Space style={{ marginTop: 24 }}>
          <Button onClick={onSaveDraft} loading={isSaving}>
            {t('common.saveDraft')}
          </Button>
          <Button type="primary" onClick={handleSubmitClick} loading={isSubmitting}>
            {t('common.submit')}
          </Button>
        </Space>
      )}
    </Card>
  );
}
