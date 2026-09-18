import { ArrowDownOutlined, ArrowUpOutlined, CopyOutlined, DeleteOutlined, WarningOutlined } from '@ant-design/icons';
import { Button, Empty, Space, Tag, Typography } from 'antd';
import type { FormFieldType } from '../../../types/form';
import { useTranslation } from '../../../i18n/LanguageContext';
import { FieldPreview } from './FieldPreview';
import type { DesignerField } from './formSchemaModel';

interface FormCanvasProps {
  fields: DesignerField[];
  selectedFieldId: string | null;
  readOnly: boolean;
  title?: string;
  mentionedFieldKeys?: ReadonlySet<string>;
  onSelectField: (internalId: string) => void;
  onMoveField: (internalId: string, direction: 'up' | 'down') => void;
  onDeleteField: (internalId: string) => void;
  onDuplicateField: (internalId: string) => void;
  onDropFieldType: (type: FormFieldType) => void;
}

// Renders fields in their current order (frontend spec §7: "the canvas is a designer preview — it
// does not need to submit real form data"). Reordering is via explicit Move Up/Down buttons —
// always available, unambiguous, and reliably testable, rather than native drag-to-reorder, which
// the Process Designer's experience with React Flow showed is hard to simulate/verify in jsdom
// (see designer/ProcessDesigner.test.tsx's notes on that). Dropping a palette item still works via
// native HTML5 drag-and-drop, the same mechanism the palette itself offers as an alternative to
// clicking.
export function FormCanvas({
  fields,
  selectedFieldId,
  readOnly,
  title,
  mentionedFieldKeys,
  onSelectField,
  onMoveField,
  onDeleteField,
  onDuplicateField,
  onDropFieldType,
}: FormCanvasProps) {
  const { t } = useTranslation();

  function handleDrop(event: React.DragEvent) {
    event.preventDefault();
    const type = event.dataTransfer.getData('application/bpm-field-type') as FormFieldType;
    if (type) onDropFieldType(type);
  }

  return (
    <div
      onDrop={readOnly ? undefined : handleDrop}
      onDragOver={readOnly ? undefined : (e) => e.preventDefault()}
      style={{ minHeight: 480, padding: 24, background: '#fafafa' }}
    >
      {title && (
        <Typography.Title level={4} style={{ marginBottom: 24 }}>
          {title}
        </Typography.Title>
      )}

      {fields.length === 0 ? (
        <Empty description={t('forms.noFieldsYet')} />
      ) : (
        <Space direction="vertical" style={{ width: '100%' }} size="middle">
          {fields.map((field, index) => {
            const selected = field.internalId === selectedFieldId;
            const hasError = mentionedFieldKeys?.has(field.key) ?? false;
            return (
              <div
                key={field.internalId}
                data-field-internal-id={field.internalId}
                onClick={() => onSelectField(field.internalId)}
                style={{
                  padding: 16,
                  borderRadius: 6,
                  background: '#fff',
                  border: `1px solid ${hasError ? '#ff4d4f' : selected ? '#1677ff' : '#d9d9d9'}`,
                  boxShadow: selected ? '0 0 0 2px rgba(22,119,255,0.15)' : undefined,
                  cursor: 'pointer',
                }}
              >
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 8 }}>
                  <Space size={4}>
                    {hasError && <WarningOutlined style={{ color: '#ff4d4f' }} />}
                    <Typography.Text strong>{field.label}</Typography.Text>
                    {field.required && <Typography.Text type="danger">*</Typography.Text>}
                    <Tag style={{ marginInlineStart: 4 }} color={field.supported ? undefined : 'warning'}>
                      {field.type}
                    </Tag>
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {field.key}
                    </Typography.Text>
                  </Space>
                  {!readOnly && (
                    <Space size={4} onClick={(e) => e.stopPropagation()}>
                      <Button
                        size="small"
                        icon={<ArrowUpOutlined />}
                        disabled={index === 0}
                        aria-label={`Move ${field.label} ${t('forms.up')}`}
                        onClick={() => onMoveField(field.internalId, 'up')}
                      />
                      <Button
                        size="small"
                        icon={<ArrowDownOutlined />}
                        disabled={index === fields.length - 1}
                        aria-label={`Move ${field.label} ${t('forms.down')}`}
                        onClick={() => onMoveField(field.internalId, 'down')}
                      />
                      <Button size="small" icon={<CopyOutlined />} aria-label={`${t('forms.duplicate')} ${field.label}`} onClick={() => onDuplicateField(field.internalId)} />
                      <Button size="small" danger icon={<DeleteOutlined />} aria-label={`${t('common.delete')} ${field.label}`} onClick={() => onDeleteField(field.internalId)} />
                    </Space>
                  )}
                </div>
                {field.supported ? (
                  <FieldPreview field={field} />
                ) : (
                  <Typography.Text type="warning" style={{ fontSize: 12 }}>
                    {t('forms.unsupportedFieldPreservedNotice')}
                  </Typography.Text>
                )}
              </div>
            );
          })}
        </Space>
      )}
    </div>
  );
}
