import { Form, Input, Modal } from 'antd';
import { useEffect } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { CreateFormDefinitionRequest } from '../../types/form';
import { useTranslation } from '../../i18n/LanguageContext';
import { useCreateFormDefinition } from './hooks';

interface CreateFormModalProps {
  open: boolean;
  onClose: () => void;
  onCreated: (id: string) => void;
}

// A newly created form definition always starts Draft — this modal never touches publish state.
export function CreateFormModal({ open, onClose, onCreated }: CreateFormModalProps) {
  const [form] = Form.useForm<CreateFormDefinitionRequest>();
  const mutation = useCreateFormDefinition();
  const { t } = useTranslation();

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
        mutation.mutate(values, {
          onSuccess: (definition) => onCreated(definition.id),
        });
      })
      .catch(() => {
        // AntD rejects validateFields() on a failed client-side rule — the form already
        // renders the field errors, so there's nothing further to do here.
      });
  }

  return (
    <Modal title={t('forms.createForm')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.create')}>
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('forms.createFormFailed')} />}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item name="name" label={t('common.name')} rules={[{ required: true, message: t('forms.nameRequired') }]}>
          <Input autoFocus />
        </Form.Item>
        <Form.Item
          name="key"
          label={t('forms.key')}
          rules={[
            { required: true, message: t('forms.keyRequired') },
            { pattern: /^[a-zA-Z0-9_-]+$/, message: t('forms.keyPattern') },
          ]}
          extra={t('forms.keyHelp')}
        >
          <Input />
        </Form.Item>
        <Form.Item name="description" label={t('common.description')}>
          <Input.TextArea rows={3} />
        </Form.Item>
        <Form.Item name="category" label={t('forms.category')}>
          <Input />
        </Form.Item>
      </Form>
    </Modal>
  );
}
