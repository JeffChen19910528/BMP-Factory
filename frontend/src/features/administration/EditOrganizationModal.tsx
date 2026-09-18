import { Alert, Button, Form, Input, Modal, Select } from 'antd';
import { useEffect, useState } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { toApiError } from '../../services/apiClient';
import type { Organization } from '../../types/organization';
import { useTranslation } from '../../i18n/LanguageContext';
import { useUpdateOrganization } from './hooks';

const CONCURRENCY_CONFLICT_CODE = 'ORGANIZATION_CONCURRENCY_CONFLICT';

interface EditOrganizationModalProps {
  open: boolean;
  organization: Organization;
  organizations: Organization[];
  onClose: () => void;
  onReload: (organizationId: string) => Promise<Organization | undefined>;
}

// Mirrors EditDepartmentModal.tsx exactly — same RowVersion/ExpectedVersion concurrency handling,
// same stale-data reload affordance. The backend re-validates self-parent/circular-hierarchy on
// every save (OrganizationService.UpdateAsync); this page only narrows the picker to exclude the
// organization's own row as an obvious-case convenience, never a re-implementation of that check.
export function EditOrganizationModal({ open, organization, organizations, onClose, onReload }: EditOrganizationModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ name: string; parentId?: string }>();
  const mutation = useUpdateOrganization(organization.id);
  const [expectedVersion, setExpectedVersion] = useState(organization.rowVersion);
  const [conflicted, setConflicted] = useState(false);

  useEffect(() => {
    if (open) {
      form.setFieldsValue({ name: organization.name, parentId: organization.parentId ?? undefined });
      setExpectedVersion(organization.rowVersion);
      setConflicted(false);
      mutation.reset();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, organization]);

  function handleOk() {
    form
      .validateFields()
      .then((values) => {
        mutation.mutate(
          { name: values.name, parentId: values.parentId ?? null, expectedVersion },
          {
            onSuccess: () => onClose(),
            onError: (error) => {
              if (toApiError(error).code === CONCURRENCY_CONFLICT_CODE) setConflicted(true);
            },
          },
        );
      })
      .catch(() => {
        // AntD rejects validateFields() on a failed client-side rule — the form already
        // renders the field errors, so there's nothing further to do here.
      });
  }

  async function handleReload() {
    const fresh = await onReload(organization.id);
    if (fresh) {
      form.setFieldsValue({ name: fresh.name, parentId: fresh.parentId ?? undefined });
      setExpectedVersion(fresh.rowVersion);
      setConflicted(false);
    }
  }

  const parentOptions = organizations.filter((o) => o.id !== organization.id);

  return (
    <Modal title={t('administration.editOrganization')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.save')}>
      {conflicted && (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 16 }}
          message={t('administration.concurrencyConflict')}
          action={
            <Button size="small" danger onClick={handleReload}>
              {t('administration.reload')}
            </Button>
          }
        />
      )}
      {mutation.isError && toApiError(mutation.error).code !== CONCURRENCY_CONFLICT_CODE && (
        <ApiErrorAlert error={mutation.error} title={t('administration.updateOrganizationError')} />
      )}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item name="name" label={t('common.name')} rules={[{ required: true, message: t('administration.nameRequired') }]}>
          <Input autoFocus />
        </Form.Item>
        <Form.Item name="parentId" label={t('administration.parentOrganization')}>
          <Select allowClear placeholder={t('administration.noParentTopLevel')} options={parentOptions.map((o) => ({ label: o.name, value: o.id }))} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
