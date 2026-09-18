import { Form, Input, Modal } from 'antd';
import { useEffect } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { ProcessDefinition, UpdateProcessDefinitionRequest } from '../../types/process';
import { useTranslation } from '../../i18n/LanguageContext';
import { useUpdateProcessDefinition } from './hooks';

interface EditProcessModalProps {
  open: boolean;
  definition: ProcessDefinition;
  onClose: () => void;
}

export function EditProcessModal({ open, definition, onClose }: EditProcessModalProps) {
  const [form] = Form.useForm<UpdateProcessDefinitionRequest>();
  const mutation = useUpdateProcessDefinition(definition.id);
  const { t } = useTranslation();

  useEffect(() => {
    if (open) {
      form.setFieldsValue({
        name: definition.name,
        description: definition.description ?? undefined,
        category: definition.category ?? undefined,
      });
      mutation.reset();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, definition]);

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
    <Modal title={t('processes.editProcess')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.save')}>
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('processes.updateProcessError')} />}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item label={t('processes.key')}>
          <Input value={definition.key} disabled />
        </Form.Item>
        <Form.Item name="name" label={t('common.name')} rules={[{ required: true, message: t('processes.nameRequired') }]}>
          <Input autoFocus />
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
