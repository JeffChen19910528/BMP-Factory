import { Form, Input, Modal, Select } from 'antd';
import { useEffect } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { Department } from '../../types/department';
import type { CreateUserRequest } from '../../types/user';
import { useTranslation } from '../../i18n/LanguageContext';
import { useCreateUser } from './hooks';

interface CreateUserModalProps {
  open: boolean;
  departments: Department[];
  onClose: () => void;
}

export function CreateUserModal({ open, departments, onClose }: CreateUserModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<CreateUserRequest>();
  const mutation = useCreateUser();

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
    <Modal title={t('administration.createUser')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.create')}>
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('administration.createUserError')} />}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item name="username" label={t('administration.username')} rules={[{ required: true, message: t('administration.usernameRequired') }]}>
          <Input autoFocus />
        </Form.Item>
        <Form.Item name="displayName" label={t('administration.displayName')} rules={[{ required: true, message: t('administration.displayNameRequired') }]}>
          <Input />
        </Form.Item>
        <Form.Item
          name="email"
          label={t('administration.email')}
          rules={[
            { required: true, message: t('administration.emailRequired') },
            { type: 'email', message: t('administration.emailInvalid') },
          ]}
        >
          <Input />
        </Form.Item>
        <Form.Item
          name="password"
          label={t('administration.password')}
          rules={[
            { required: true, message: t('administration.passwordRequired') },
            { min: 8, message: t('administration.passwordMinLength') },
          ]}
        >
          <Input.Password />
        </Form.Item>
        <Form.Item name="departmentId" label={t('administration.department')}>
          <Select
            allowClear
            placeholder={t('administration.noDepartment')}
            options={departments.map((d) => ({ label: d.name, value: d.id }))}
          />
        </Form.Item>
      </Form>
    </Modal>
  );
}
