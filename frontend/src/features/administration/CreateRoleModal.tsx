import { Form, Input, Modal } from 'antd';
import { useEffect } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { CreateRoleRequest } from '../../types/role';
import { useTranslation } from '../../i18n/LanguageContext';
import { useCreateRole } from './hooks';

interface CreateRoleModalProps {
  open: boolean;
  onClose: () => void;
}

export function CreateRoleModal({ open, onClose }: CreateRoleModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<CreateRoleRequest>();
  const mutation = useCreateRole();

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
        mutation.mutate(values, { onSuccess: () => onClose() });
      })
      .catch(() => {
        // AntD rejects validateFields() on a failed client-side rule — the form already
        // renders the field errors, so there's nothing further to do here.
      });
  }

  return (
    <Modal title={t('administration.createRole')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.create')}>
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('administration.createRoleError')} />}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item name="name" label={t('common.name')} rules={[{ required: true, message: t('administration.nameRequired') }]}>
          <Input autoFocus />
        </Form.Item>
      </Form>
    </Modal>
  );
}
