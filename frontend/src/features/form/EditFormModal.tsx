import { Form, Input, Modal } from 'antd';
import { useEffect } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { FormDefinition, UpdateFormDefinitionRequest } from '../../types/form';
import { useTranslation } from '../../i18n/LanguageContext';
import { useUpdateFormDefinition } from './hooks';

interface EditFormModalProps {
  open: boolean;
  definition: FormDefinition;
  onClose: () => void;
}

export function EditFormModal({ open, definition, onClose }: EditFormModalProps) {
  const [form] = Form.useForm<UpdateFormDefinitionRequest>();
  const mutation = useUpdateFormDefinition(definition.id);
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
    <Modal title={t('forms.editForm')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.save')}>
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('forms.editFormFailed')} />}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item label={t('forms.key')}>
          <Input value={definition.key} disabled />
        </Form.Item>
        <Form.Item name="name" label={t('common.name')} rules={[{ required: true, message: t('forms.nameRequired') }]}>
          <Input autoFocus />
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
