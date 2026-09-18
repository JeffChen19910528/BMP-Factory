import { Form, Input, Modal, Select } from 'antd';
import { useEffect } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { Organization } from '../../types/organization';
import { useTranslation } from '../../i18n/LanguageContext';
import { useCreateOrganization } from './hooks';

interface CreateOrganizationModalProps {
  open: boolean;
  organizations: Organization[];
  onClose: () => void;
}

export function CreateOrganizationModal({ open, organizations, onClose }: CreateOrganizationModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ name: string; parentId?: string }>();
  const mutation = useCreateOrganization();

  useEffect(() => {
    if (open) {
      form.resetFields();
      mutation.reset();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  function handleOk() {
    form
      .validateFields()
      .then((values) => {
        mutation.mutate(
          { name: values.name, parentId: values.parentId ?? null },
          { onSuccess: () => onClose() },
        );
      })
      .catch(() => {
        // AntD rejects validateFields() on a failed client-side rule — the form already
        // renders the field errors, so there's nothing further to do here.
      });
  }

  return (
    <Modal title={t('administration.createOrganization')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.create')}>
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('administration.createOrganizationError')} />}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item name="name" label={t('common.name')} rules={[{ required: true, message: t('administration.nameRequired') }]}>
          <Input autoFocus />
        </Form.Item>
        <Form.Item name="parentId" label={t('administration.parentOrganization')}>
          <Select allowClear placeholder={t('administration.noParentTopLevel')} options={organizations.map((o) => ({ label: o.name, value: o.id }))} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
