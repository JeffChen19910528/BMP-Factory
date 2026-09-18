import { useState } from 'react';
import { UploadOutlined } from '@ant-design/icons';
import { Button, Checkbox, DatePicker, Input, InputNumber, Radio, Select, Typography } from 'antd';
import dayjs, { type Dayjs } from 'dayjs';
import { useTranslation } from '../../../i18n/LanguageContext';
import type { DesignerField } from './formSchemaModel';

interface FieldPreviewProps {
  field: DesignerField;
  // false (the default) renders a disabled, read-only-looking control — used inline on the
  // canvas, where the row exists to be selected/configured, not filled in. true renders a fully
  // interactive control with local-only state — used by Preview mode (frontend spec §17: "render
  // the form as a user would see it... no real submission, no persistence" — interactive typing
  // is fine as long as nothing is ever sent anywhere or kept once Preview closes).
  interactive?: boolean;
  // Phase 5.4.2: when the Designer's Preview needs to react to a field's value (to re-evaluate
  // visibility/enabled/required/calculated rules live), it lifts this control's value up instead
  // of letting FieldPreview manage it locally. Omitted, this falls back to the pre-5.4.2
  // uncontrolled behavior (own internal useState) — FormCanvas's inline row preview never passes
  // these, so its rendering is unaffected by this change.
  controlledValue?: unknown;
  onControlledChange?: (value: unknown) => void;
  // Phase 5.4.2 §10/§11: reflects the rule engine's Enabled outcome for this field, on top of
  // `interactive`/`disabled` — a field can be visible and interactive-mode yet still disabled by
  // an Enabled-type rule.
  ruleDisabled?: boolean;
}

// The one place that knows how to render each FormFieldType as an actual input control — shared
// by FormCanvas's inline row preview and the Designer's Preview mode, so the two never drift on
// what a given field type looks like.
export function FieldPreview({ field, interactive = false, controlledValue, onControlledChange, ruleDisabled = false }: FieldPreviewProps) {
  const { t } = useTranslation();
  const isControlled = onControlledChange !== undefined;
  const [localValue, setLocalValue] = useState<unknown>(field.defaultValue ?? undefined);
  const value = isControlled ? controlledValue : localValue;
  const setValue = isControlled ? onControlledChange! : setLocalValue;
  const disabled = !interactive || ruleDisabled;
  const placeholder = field.placeholder ?? undefined;

  switch (field.type) {
    case 'Text':
      return <Input disabled={disabled} placeholder={placeholder} value={interactive ? (value as string) : field.defaultValue ?? ''} onChange={(e) => setValue(e.target.value)} />;
    case 'Textarea':
      return (
        <Input.TextArea
          disabled={disabled}
          placeholder={placeholder}
          rows={3}
          value={interactive ? (value as string) : field.defaultValue ?? ''}
          onChange={(e) => setValue(e.target.value)}
        />
      );
    case 'Number':
      return (
        <InputNumber
          style={{ width: '100%' }}
          disabled={disabled}
          placeholder={placeholder}
          min={field.validation?.minValue ?? undefined}
          max={field.validation?.maxValue ?? undefined}
          value={interactive ? (value as number) : field.defaultValue ? Number(field.defaultValue) : undefined}
          onChange={(v) => setValue(v)}
        />
      );
    case 'Currency':
      return (
        <InputNumber
          style={{ width: '100%' }}
          disabled={disabled}
          placeholder={placeholder}
          prefix="$"
          min={field.validation?.minValue ?? undefined}
          max={field.validation?.maxValue ?? undefined}
          value={interactive ? (value as number) : field.defaultValue ? Number(field.defaultValue) : undefined}
          onChange={(v) => setValue(v)}
        />
      );
    case 'Date':
    case 'DateTime': {
      // Stored/exchanged as an ISO string (matches FormDataValidator's `DateTime.TryParse` check
      // on the backend) — DatePicker itself only speaks dayjs objects, so this is a pure
      // presentation-layer conversion at the edges, not a second value representation.
      const isoValue = interactive ? (value as string | undefined) : undefined;
      const dayjsValue: Dayjs | null = isoValue ? dayjs(isoValue) : null;
      return (
        <DatePicker
          style={{ width: '100%' }}
          disabled={disabled}
          placeholder={placeholder}
          showTime={field.type === 'DateTime'}
          value={dayjsValue}
          onChange={(next) => setValue(next ? next.toISOString() : undefined)}
        />
      );
    }
    case 'Select':
      return (
        <Select
          style={{ width: '100%' }}
          disabled={disabled}
          placeholder={placeholder ?? t('forms.selectAnOption')}
          options={(field.options ?? []).map((o) => ({ label: o.label, value: o.value }))}
          value={interactive ? (value as string) : undefined}
          onChange={(v) => setValue(v)}
        />
      );
    case 'Radio':
      return (
        <Radio.Group
          disabled={disabled}
          options={(field.options ?? []).map((o) => ({ label: o.label, value: o.value }))}
          value={interactive ? value : undefined}
          onChange={(e) => setValue(e.target.value)}
        />
      );
    case 'Checkbox':
      return (
        <Checkbox disabled={disabled} checked={interactive ? Boolean(value) : false} onChange={(e) => setValue(e.target.checked)}>
          {field.label}
        </Checkbox>
      );
    case 'User':
      return <Select style={{ width: '100%' }} disabled={disabled} placeholder={placeholder ?? t('forms.selectAUser')} options={[]} />;
    case 'Department':
      return <Select style={{ width: '100%' }} disabled={disabled} placeholder={placeholder ?? t('forms.selectADepartment')} options={[]} />;
    case 'File':
      return (
        <Button disabled={disabled} icon={<UploadOutlined />}>
          {t('forms.chooseFile')}
        </Button>
      );
    default:
      return <Typography.Text type="secondary">{t('forms.unsupportedFieldType')}: {field.type}</Typography.Text>;
  }
}
