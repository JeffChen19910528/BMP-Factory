import { Alert, Button, Checkbox, Divider, Empty, Input, InputNumber, Space, Typography } from 'antd';
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons';
import type { FormFieldOption, FormRule } from '../../../types/form';
import { useTranslation } from '../../../i18n/LanguageContext';
import { NUMERIC_TYPES, OPTION_BASED_TYPES, TEXT_LIKE_TYPES, type DesignerField } from './formSchemaModel';
import { RuleBuilder } from './RuleBuilder';

const KEY_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*$/;

interface PropertiesPanelProps {
  field: DesignerField | null;
  // Every field in the schema, selected one excluded where relevant — used both for key-
  // uniqueness validation (frontend spec §10) and, as of Phase 5.4.2, as the Rule Builder's list
  // of possible condition source fields.
  allFields?: DesignerField[];
  // Every other field's key in this schema — used to validate uniqueness client-side (frontend
  // spec §10) without needing a round trip; the backend's DUPLICATE_FIELD_KEY check remains
  // authoritative at Validate/Save time regardless.
  existingKeys: ReadonlySet<string>;
  readOnly: boolean;
  onChange: (internalId: string, partial: Partial<DesignerField>) => void;
  onDelete: (internalId: string) => void;
  // Phase 5.4.2 — the whole schema's rules (not just this field's), since the Rule Builder needs
  // to read/write the top-level list, and onRulesChange replaces it wholesale (mirrors onChange
  // for fields — one commit per meaningful edit, for undo/redo).
  rules?: FormRule[];
  onRulesChange?: (rules: FormRule[]) => void;
}

// Only ever shows properties appropriate to the selected field's type (frontend spec §8) — the
// source of truth for which extras apply is FormSchemaValidator's own rules (length/pattern
// validation only for Text/Textarea, minValue/maxValue only for Number/Currency, Options only for
// Select/Radio), not a guess: showing a control the backend would reject is worse than not
// showing it at all.
export function PropertiesPanel({ field, allFields, existingKeys, readOnly, onChange, onDelete, rules, onRulesChange }: PropertiesPanelProps) {
  const { t } = useTranslation();

  if (!field) {
    return (
      <div style={{ padding: 24 }}>
        <Empty description={t('forms.selectFieldToEdit')} />
      </div>
    );
  }

  if (!field.supported) {
    return (
      <div style={{ padding: 16 }}>
        <Typography.Title level={5}>{field.label}</Typography.Title>
        <Typography.Paragraph type="warning">
          {t('forms.unsupportedFieldTypeNotice1')} ("{field.type}") {t('forms.unsupportedFieldTypeNotice2')}
        </Typography.Paragraph>
        <Typography.Paragraph>
          <pre style={{ background: '#fafafa', padding: 8, borderRadius: 4, fontSize: 12 }}>
            {JSON.stringify(field.raw, null, 2)}
          </pre>
        </Typography.Paragraph>
      </div>
    );
  }

  function update(partial: Partial<DesignerField>) {
    onChange(field!.internalId, partial);
  }

  const keyEmpty = field.key.trim() === '';
  const keyInvalidFormat = !keyEmpty && !KEY_PATTERN.test(field.key);
  const keyDuplicate = !keyEmpty && existingKeys.has(field.key);
  const keyError = keyEmpty ? t('forms.keyRequired') : keyInvalidFormat ? t('forms.keyInvalidFormat') : keyDuplicate ? t('forms.keyDuplicate') : null;

  const isTextLike = TEXT_LIKE_TYPES.includes(field.type);
  const isNumeric = NUMERIC_TYPES.includes(field.type);
  const isOptionBased = OPTION_BASED_TYPES.includes(field.type);

  return (
    <div style={{ padding: 16 }}>
      <Space style={{ width: '100%', justifyContent: 'space-between' }}>
        <Typography.Title level={5} style={{ margin: 0 }}>
          {t(`forms.fieldType.${field.type}`)}
        </Typography.Title>
        {!readOnly && (
          <Button danger size="small" icon={<DeleteOutlined />} onClick={() => onDelete(field!.internalId)}>
            {t('common.delete')}
          </Button>
        )}
      </Space>
      <Divider style={{ margin: '12px 0' }} />

      <label>
        <Typography.Text strong>{t('forms.key')}</Typography.Text>
        <Input
          value={field.key}
          disabled={readOnly}
          status={keyError ? 'error' : undefined}
          onChange={(e) => update({ key: e.target.value })}
          style={{ marginTop: 4 }}
        />
        {keyError ? (
          <Typography.Text type="danger" style={{ fontSize: 12 }}>
            {keyError}
          </Typography.Text>
        ) : (
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('forms.keyRenameHint')}
          </Typography.Text>
        )}
      </label>

      <label style={{ display: 'block', marginTop: 16 }}>
        <Typography.Text strong>{t('forms.label')}</Typography.Text>
        <Input value={field.label} disabled={readOnly} onChange={(e) => update({ label: e.target.value })} style={{ marginTop: 4 }} />
      </label>

      <label style={{ display: 'block', marginTop: 16 }}>
        <Typography.Text strong>{t('common.description')}</Typography.Text>
        <Input.TextArea
          rows={2}
          value={field.description ?? ''}
          disabled={readOnly}
          onChange={(e) => update({ description: e.target.value || null })}
          style={{ marginTop: 4 }}
        />
      </label>

      <div style={{ marginTop: 16 }}>
        <Checkbox checked={field.required} disabled={readOnly} onChange={(e) => update({ required: e.target.checked })}>
          {t('common.required')}
        </Checkbox>
      </div>

      {field.type !== 'Checkbox' && field.type !== 'File' && (
        <label style={{ display: 'block', marginTop: 16 }}>
          <Typography.Text strong>{t('forms.placeholder')}</Typography.Text>
          <Input
            value={field.placeholder ?? ''}
            disabled={readOnly}
            onChange={(e) => update({ placeholder: e.target.value || null })}
            style={{ marginTop: 4 }}
          />
        </label>
      )}

      {(field.type === 'Text' || field.type === 'Textarea' || isNumeric) && (
        <label style={{ display: 'block', marginTop: 16 }}>
          <Typography.Text strong>{t('forms.defaultValue')}</Typography.Text>
          <Input
            value={field.defaultValue ?? ''}
            disabled={readOnly}
            onChange={(e) => update({ defaultValue: e.target.value || null })}
            style={{ marginTop: 4 }}
          />
        </label>
      )}

      {isOptionBased && (
        <OptionsEditor field={field} readOnly={readOnly} onChange={update} />
      )}

      {isTextLike && (
        <div style={{ marginTop: 16 }}>
          <Typography.Text strong>{t('forms.validation')}</Typography.Text>
          <Space style={{ marginTop: 4 }}>
            <label>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {t('forms.minLength')}
              </Typography.Text>
              <InputNumber
                min={0}
                disabled={readOnly}
                value={field.validation?.minLength ?? undefined}
                onChange={(v) => update({ validation: { ...field.validation, minLength: v ?? null } })}
                style={{ display: 'block', width: 110 }}
              />
            </label>
            <label>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {t('forms.maxLength')}
              </Typography.Text>
              <InputNumber
                min={0}
                disabled={readOnly}
                value={field.validation?.maxLength ?? undefined}
                onChange={(v) => update({ validation: { ...field.validation, maxLength: v ?? null } })}
                style={{ display: 'block', width: 110 }}
              />
            </label>
          </Space>
          {field.validation?.maxLength != null && field.validation?.minLength != null && field.validation.maxLength < field.validation.minLength && (
            <Alert type="error" showIcon style={{ marginTop: 8 }} message={t('forms.maxLengthMustBeGteMinLength')} />
          )}
        </div>
      )}

      {isNumeric && (
        <div style={{ marginTop: 16 }}>
          <Typography.Text strong>{t('forms.validation')}</Typography.Text>
          <Space style={{ marginTop: 4 }}>
            <label>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {t('forms.minValue')}
              </Typography.Text>
              <InputNumber
                disabled={readOnly}
                value={field.validation?.minValue ?? undefined}
                onChange={(v) => update({ validation: { ...field.validation, minValue: v ?? null } })}
                style={{ display: 'block', width: 110 }}
              />
            </label>
            <label>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {t('forms.maxValue')}
              </Typography.Text>
              <InputNumber
                disabled={readOnly}
                value={field.validation?.maxValue ?? undefined}
                onChange={(v) => update({ validation: { ...field.validation, maxValue: v ?? null } })}
                style={{ display: 'block', width: 110 }}
              />
            </label>
          </Space>
          {field.validation?.maxValue != null && field.validation?.minValue != null && field.validation.maxValue < field.validation.minValue && (
            <Alert type="error" showIcon style={{ marginTop: 8 }} message={t('forms.maxValueMustBeGteMinValue')} />
          )}
        </div>
      )}

      {rules && onRulesChange && (
        <RuleBuilder field={field} allFields={allFields ?? []} rules={rules} readOnly={readOnly} onChange={onRulesChange} />
      )}
    </div>
  );
}

function OptionsEditor({
  field,
  readOnly,
  onChange,
}: {
  field: DesignerField;
  readOnly: boolean;
  onChange: (partial: Partial<DesignerField>) => void;
}) {
  const { t } = useTranslation();
  const options = field.options ?? [];

  function updateOption(index: number, next: FormFieldOption) {
    const copy = options.slice();
    copy[index] = next;
    onChange({ options: copy });
  }

  function removeOption(index: number) {
    onChange({ options: options.filter((_, i) => i !== index) });
  }

  function addOption() {
    const n = options.length + 1;
    onChange({ options: [...options, { value: `option${n}`, label: `Option ${n}` }] });
  }

  return (
    <div style={{ marginTop: 16 }}>
      <Typography.Text strong>{t('forms.options')}</Typography.Text>
      <Space direction="vertical" style={{ width: '100%', marginTop: 4 }}>
        {options.map((option, index) => {
          const duplicate = options.some((o, i) => i !== index && o.value === option.value);
          return (
            <div key={index}>
              <Space.Compact block>
                <Input
                  placeholder={t('forms.label')}
                  value={option.label}
                  disabled={readOnly}
                  onChange={(e) => updateOption(index, { value: option.value, label: e.target.value })}
                />
                <Input
                  placeholder={t('forms.value')}
                  status={duplicate || option.value.trim() === '' ? 'error' : undefined}
                  value={option.value}
                  disabled={readOnly}
                  onChange={(e) => updateOption(index, { value: e.target.value, label: option.label })}
                />
                {!readOnly && <Button icon={<DeleteOutlined />} onClick={() => removeOption(index)} />}
              </Space.Compact>
              {duplicate && (
                <Typography.Text type="danger" style={{ fontSize: 12 }}>
                  {t('forms.duplicateOptionValue')}
                </Typography.Text>
              )}
            </div>
          );
        })}
        {!readOnly && (
          <Button block icon={<PlusOutlined />} onClick={addOption}>
            {t('forms.addOption')}
          </Button>
        )}
        {options.length === 0 && (
          <Typography.Text type="danger" style={{ fontSize: 12 }}>
            {t('forms.atLeastOneOptionRequired')}
          </Typography.Text>
        )}
      </Space>
    </div>
  );
}
