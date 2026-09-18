import { Form, Input, Modal, Select } from 'antd';
import { useEffect } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { Department } from '../../types/department';
import type { Organization } from '../../types/organization';
import type { User } from '../../types/user';
import { useTranslation } from '../../i18n/LanguageContext';
import { useCreateDepartment } from './hooks';

interface CreateDepartmentModalProps {
  open: boolean;
  departments: Department[];
  organizations: Organization[];
  users: User[];
  onClose: () => void;
}

export function CreateDepartmentModal({ open, departments, organizations, users, onClose }: CreateDepartmentModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ name: string; organizationId: string; parentId?: string; managerUserId?: string }>();
  const mutation = useCreateDepartment();

  useEffect(() => {
    if (open) {
      form.resetFields();
      if (organizations.length === 1) {
        form.setFieldValue('organizationId', organizations[0].id);
      }
      mutation.reset();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  function handleOk() {
    form
      .validateFields()
      .then((values) => {
        mutation.mutate(
          {
            name: values.name,
            organizationId: values.organizationId,
            parentId: values.parentId ?? null,
            managerUserId: values.managerUserId ?? null,
          },
          { onSuccess: () => onClose() },
        );
      })
      .catch(() => {
        // AntD rejects validateFields() on a failed client-side rule — the form already
        // renders the field errors, so there's nothing further to do here.
      });
  }

  return (
    <Modal title={t('administration.createDepartment')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.create')}>
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('administration.createDepartmentError')} />}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item name="name" label={t('common.name')} rules={[{ required: true, message: t('administration.nameRequired') }]}>
          <Input autoFocus />
        </Form.Item>
        <Form.Item name="organizationId" label={t('administration.organization')} rules={[{ required: true, message: t('administration.organizationRequired') }]}>
          <Select options={organizations.map((o) => ({ label: o.name, value: o.id }))} />
        </Form.Item>
        <Form.Item name="parentId" label={t('administration.parentDepartment')}>
          <Select allowClear placeholder={t('administration.noParentTopLevel')} options={departments.map((d) => ({ label: d.name, value: d.id }))} />
        </Form.Item>
        <Form.Item name="managerUserId" label={t('administration.manager')}>
          <Select
            allowClear
            placeholder={t('administration.noManager')}
            showSearch
            optionFilterProp="label"
            options={users.map((u) => ({ label: u.displayName, value: u.id }))}
          />
        </Form.Item>
      </Form>
    </Modal>
  );
}
