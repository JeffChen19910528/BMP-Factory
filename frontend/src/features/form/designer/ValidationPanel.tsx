import { Alert, Typography } from 'antd';
import { CheckCircleOutlined, WarningOutlined } from '@ant-design/icons';
import type { WorkflowValidationResult } from '../../../types/form';
import { useTranslation } from '../../../i18n/LanguageContext';
import type { DesignerField } from './formSchemaModel';

export interface ResolvedFormValidationError {
  code: string;
  message: string;
  field: { key: string; label: string; type: string } | null;
}

// Mirrors the Process Designer's ValidationPanel/resolveValidationErrors exactly (same heuristic,
// same "never fabricate a mapping" rule): FormSchemaValidator's errors only carry {code,
// message}, no structured field key — every single-quoted token in the message is checked
// against the schema's current field keys, and the first match wins. An error that matches
// nothing resolves to `field: null` and renders in the global list rather than being guessed at.
export function resolveValidationErrors(
  result: WorkflowValidationResult | null,
  fields: DesignerField[],
): ResolvedFormValidationError[] {
  if (!result) return [];
  const byKey = new Map(fields.map((f) => [f.key, f]));

  return result.errors.map((error) => {
    for (const match of error.message.matchAll(/'([^']+)'/g)) {
      const field = byKey.get(match[1]);
      if (field) {
        return { code: error.code, message: error.message, field: { key: field.key, label: field.label, type: field.type } };
      }
    }
    return { code: error.code, message: error.message, field: null };
  });
}

interface ValidationPanelProps {
  result: WorkflowValidationResult | null;
  fields: DesignerField[];
  onSelectField: (fieldKey: string) => void;
}

// Dedicated validation result area (frontend spec §16) — backend validation is authoritative
// throughout; this only renders whatever FormSchemaValidator (via POST
// /api/form-definitions/validate) already decided, it never re-judges validity itself.
export function ValidationPanel({ result, fields, onSelectField }: ValidationPanelProps) {
  const { t } = useTranslation();
  if (!result) return null;

  if (result.isValid) {
    return (
      <Alert style={{ margin: '8px 16px 0' }} type="success" showIcon icon={<CheckCircleOutlined />} message={t('forms.schemaValidNotice')} />
    );
  }

  const resolved = resolveValidationErrors(result, fields);

  return (
    <div style={{ margin: '8px 16px 0', border: '1px solid #ffccc7', borderRadius: 6, background: '#fff2f0' }}>
      <div style={{ padding: '8px 12px', borderBottom: '1px solid #ffccc7', fontWeight: 600 }}>{t('forms.formValidation')}</div>
      <ul style={{ margin: 0, padding: '4px 0', listStyle: 'none' }}>
        {resolved.map((error, i) => (
          <li
            key={i}
            onClick={error.field ? () => onSelectField(error.field!.key) : undefined}
            style={{
              padding: '6px 12px',
              cursor: error.field ? 'pointer' : 'default',
              borderBottom: i < resolved.length - 1 ? '1px solid #ffe7e4' : undefined,
            }}
          >
            <WarningOutlined style={{ color: '#cf1322', marginInlineEnd: 6 }} />
            {error.field ? (
              <Typography.Text>
                <Typography.Text strong>
                  {error.field.type} "{error.field.label}"
                </Typography.Text>
                {' — '}
                {error.message}
              </Typography.Text>
            ) : (
              <Typography.Text>{error.message}</Typography.Text>
            )}
            <Typography.Text type="secondary" style={{ marginInlineStart: 8, fontSize: 12 }}>
              {error.code}
            </Typography.Text>
          </li>
        ))}
      </ul>
    </div>
  );
}
