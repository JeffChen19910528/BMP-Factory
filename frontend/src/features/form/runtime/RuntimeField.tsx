import { useState } from 'react';
import { DeleteOutlined, PaperClipOutlined, UploadOutlined } from '@ant-design/icons';
import { Alert, Button, List, Select, Space, Typography, Upload } from 'antd';
import type { UploadRequestOption } from '@rc-component/upload';
import { useDepartments } from '../../../hooks/useDepartments';
import { useUsersById } from '../../../hooks/useUsers';
import { attachmentDownloadUrl } from '../../../services/formInstanceService';
import type { FormFieldDefinition } from '../../../types/form';
import { useTranslation } from '../../../i18n/LanguageContext';
import { FieldPreview } from '../designer/FieldPreview';
import type { DesignerField } from '../designer/formSchemaModel';
import { useAttachments, useDeleteAttachment, useUploadAttachment } from './hooks';

interface RuntimeFieldProps {
  field: FormFieldDefinition;
  value: unknown;
  disabled: boolean;
  onChange: (value: unknown) => void;
  formInstanceId: string;
}

// Renders one field's actual input control for the Form Runtime. Phase 4's basic types
// (Text/Textarea/Number/Currency/Date/DateTime/Select/Radio/Checkbox) delegate straight to the
// existing Designer FieldPreview in controlled mode — reused, not reimplemented, per this phase's
// explicit "do not duplicate Form Designer field definitions" instruction. User, Department, and
// File are the three types that need a *real* backing data source at runtime (an actual user/
// department list, an actual uploaded file) rather than the Designer's inert placeholder — those
// three are the only cases this component adds anything beyond FieldPreview.
export function RuntimeField({ field, value, disabled, onChange, formInstanceId }: RuntimeFieldProps) {
  if (field.type === 'User') {
    return <UserField value={value as string | undefined} disabled={disabled} onChange={onChange} />;
  }
  if (field.type === 'Department') {
    return <DepartmentField value={value as string | undefined} disabled={disabled} onChange={onChange} />;
  }
  if (field.type === 'File') {
    return <FileField field={field} value={value} disabled={disabled} onChange={onChange} formInstanceId={formInstanceId} />;
  }

  const designerField: DesignerField = {
    internalId: field.key,
    key: field.key,
    type: field.type,
    label: field.label,
    description: field.description ?? null,
    required: !!field.required,
    defaultValue: field.defaultValue ?? null,
    placeholder: field.placeholder ?? null,
    options: field.options ?? null,
    validation: field.validation ?? null,
    readOnly: field.readOnly ?? false,
    supported: true,
    raw: field,
  };

  return <FieldPreview field={designerField} interactive controlledValue={value} onControlledChange={onChange} ruleDisabled={disabled} />;
}

// Reuses the existing GET /api/users the Process Designer's assignment picker already calls
// (useUsersById) — a real, backend-issued identity, never a free-text input a client could spoof.
function UserField({ value, disabled, onChange }: { value: string | undefined; disabled: boolean; onChange: (v: string | undefined) => void }) {
  const { users, isLoading } = useUsersById();
  const { t } = useTranslation();
  return (
    <Select
      showSearch
      allowClear
      virtual={false}
      style={{ width: '100%' }}
      disabled={disabled}
      loading={isLoading}
      placeholder={t('forms.selectAUser')}
      value={value}
      options={users.map((u) => ({ label: u.displayName, value: u.id }))}
      filterOption={(input, option) => (option?.label ?? '').toLowerCase().includes(input.toLowerCase())}
      onChange={(v) => onChange(v ?? undefined)}
    />
  );
}

// Reuses the existing GET /api/departments the Process Designer already calls (useDepartments) —
// no department data is duplicated into the Form Schema.
function DepartmentField({ value, disabled, onChange }: { value: string | undefined; disabled: boolean; onChange: (v: string | undefined) => void }) {
  const { departments, isLoading } = useDepartments();
  const { t } = useTranslation();
  return (
    <Select
      showSearch
      allowClear
      virtual={false}
      style={{ width: '100%' }}
      disabled={disabled}
      loading={isLoading}
      placeholder={t('forms.selectADepartment')}
      value={value}
      options={departments.map((d) => ({ label: d.name, value: d.id }))}
      filterOption={(input, option) => (option?.label ?? '').toLowerCase().includes(input.toLowerCase())}
      onChange={(v) => onChange(v ?? undefined)}
    />
  );
}

// A File field's value is the attachment id(s) (string or string[]) — matching
// FormDataValidator's own contract ("must be an attachment id or array of attachment ids") — never
// raw bytes inside FormData JSON. Actual bytes live in MinIO via the existing
// AttachmentService/AttachmentsController, reused as-is (upload/list/download/delete).
function FileField({ field, value, disabled, onChange, formInstanceId }: { field: FormFieldDefinition; value: unknown; disabled: boolean; onChange: (v: unknown) => void; formInstanceId: string }) {
  const attachmentsQuery = useAttachments(formInstanceId);
  const uploadMutation = useUploadAttachment(formInstanceId);
  const deleteMutation = useDeleteAttachment(formInstanceId);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const { t } = useTranslation();

  const selectedIds = Array.isArray(value) ? (value as string[]) : typeof value === 'string' && value ? [value] : [];
  const attachmentsById = new Map((attachmentsQuery.data ?? []).map((a) => [a.id, a]));
  const selected = selectedIds.map((id) => attachmentsById.get(id)).filter((a): a is NonNullable<typeof a> => !!a);

  async function handleUpload(options: UploadRequestOption) {
    setUploadError(null);
    try {
      const attachment = await uploadMutation.mutateAsync(options.file as File);
      onChange(field.key.endsWith('s') ? [...selectedIds, attachment.id] : attachment.id);
      options.onSuccess?.(attachment);
    } catch (err) {
      setUploadError(err instanceof Error ? err.message : t('forms.uploadFailed'));
      options.onError?.(err as Error);
    }
  }

  function removeAttachment(id: string) {
    deleteMutation.mutate(id);
    onChange(Array.isArray(value) ? selectedIds.filter((x) => x !== id) : undefined);
  }

  return (
    <div>
      {!disabled && (
        <Upload customRequest={handleUpload} showUploadList={false} disabled={uploadMutation.isPending}>
          <Button icon={<UploadOutlined />} loading={uploadMutation.isPending}>
            {t('forms.chooseFile')}
          </Button>
        </Upload>
      )}
      {uploadError && <Alert style={{ marginTop: 8 }} type="error" showIcon message={uploadError} />}
      <List
        style={{ marginTop: 8 }}
        size="small"
        dataSource={selected}
        renderItem={(attachment) => (
          <List.Item
            actions={
              disabled
                ? []
                : [<Button key="remove" size="small" danger icon={<DeleteOutlined />} aria-label={`${t('common.delete')} ${attachment.fileName}`} onClick={() => removeAttachment(attachment.id)} />]
            }
          >
            <Space>
              <PaperClipOutlined />
              <a href={attachmentDownloadUrl(attachment.id)} target="_blank" rel="noreferrer">
                {attachment.fileName}
              </a>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {(attachment.size / 1024).toFixed(1)} KB
              </Typography.Text>
            </Space>
          </List.Item>
        )}
      />
    </div>
  );
}
