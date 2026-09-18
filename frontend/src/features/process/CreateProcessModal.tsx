import { Form, Input, Modal } from 'antd';
import { useEffect } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { CreateProcessDefinitionRequest } from '../../types/process';
import { useTranslation } from '../../i18n/LanguageContext';
import { useCreateProcessDefinition } from './hooks';

interface CreateProcessModalProps {
  open: boolean;
  onClose: () => void;
  onCreated: (id: string) => void;
}

// A newly created process is always Draft — this modal never touches publish state
// (frontend spec §5: "Do not automatically publish anything").
export function CreateProcessModal({ open, onClose, onCreated }: CreateProcessModalProps) {
  const [form] = Form.useForm<CreateProcessDefinitionRequest>();
  const mutation = useCreateProcessDefinition();
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
    <Modal
      title={t('processes.createProcess')}
      open={open}
      onCancel={onClose}
      onOk={handleOk}
      confirmLoading={mutation.isPending}
      okText={t('common.create')}
    >
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('processes.createProcessError')} />}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item name="name" label={t('common.name')} rules={[{ required: true, message: t('processes.nameRequired') }]}>
          <Input autoFocus />
        </Form.Item>
        <Form.Item
          name="key"
          label={t('processes.key')}
          rules={[
            { required: true, message: t('processes.keyRequired') },
            { pattern: /^[a-zA-Z0-9_-]+$/, message: t('processes.keyPatternError') },
          ]}
          extra={t('processes.keyExtra')}
        >
          <Input />
        </Form.Item>
        <Form.Item name="description" label={t('common.description')}>
          <Input.TextArea rows={3} />
        </Form.Item>
        <Form.Item name="category" label={t('processes.category')}>
          <Input />
        </Form.Item>
      </Form>
    </Modal>
  );
}
