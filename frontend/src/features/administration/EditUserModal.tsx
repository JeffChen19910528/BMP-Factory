import { Alert, Button, Form, Input, Modal, Select, Switch } from 'antd';
import { ExclamationCircleOutlined } from '@ant-design/icons';
import { useEffect, useState } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { toApiError } from '../../services/apiClient';
import type { Department } from '../../types/department';
import type { UpdateUserRequest, User } from '../../types/user';
import { useTranslation } from '../../i18n/LanguageContext';
import { useUpdateUser } from './hooks';

const CONCURRENCY_CONFLICT_CODE = 'USER_CONCURRENCY_CONFLICT';

interface EditUserModalProps {
  open: boolean;
  user: User;
  departments: Department[];
  onClose: () => void;
  // Re-fetches the users list and returns this user's latest row — the safe recovery path after
  // a 409 (Phase 5.5.2 §24's "Data was modified by another administrator." + [Reload] pattern,
  // mirroring ProcessVersionEditorPage's handleReload).
  onReload: (userId: string) => Promise<User | undefined>;
}

export function EditUserModal({ open, user, departments, onClose, onReload }: EditUserModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<Omit<UpdateUserRequest, 'expectedVersion'> & { departmentId?: string }>();
  const mutation = useUpdateUser(user.id);
  const [expectedVersion, setExpectedVersion] = useState(user.rowVersion);
  const [conflicted, setConflicted] = useState(false);

  useEffect(() => {
    if (open) {
      form.setFieldsValue({
        displayName: user.displayName,
        email: user.email,
        departmentId: user.departmentId ?? undefined,
        isActive: user.isActive,
      });
      setExpectedVersion(user.rowVersion);
      setConflicted(false);
      mutation.reset();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, user]);

  function handleOk() {
    form
      .validateFields()
      .then((values) => {
        mutation.mutate(
          {
            displayName: values.displayName,
            email: values.email,
            departmentId: values.departmentId ?? null,
            isActive: values.isActive,
            expectedVersion,
          },
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
    const fresh = await onReload(user.id);
    if (fresh) {
      form.setFieldsValue({
        displayName: fresh.displayName,
        email: fresh.email,
        departmentId: fresh.departmentId ?? undefined,
        isActive: fresh.isActive,
      });
      setExpectedVersion(fresh.rowVersion);
      setConflicted(false);
    }
  }

  return (
    <Modal title={t('administration.editUser')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.save')}>
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
        <ApiErrorAlert error={mutation.error} title={t('administration.updateUserError')} />
      )}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item label={t('administration.username')}>
          <Input value={user.username} disabled />
        </Form.Item>
        <Form.Item name="displayName" label={t('administration.displayName')} rules={[{ required: true, message: t('administration.displayNameRequired') }]}>
          <Input autoFocus />
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
        <Form.Item name="departmentId" label={t('administration.department')}>
          <Select
            allowClear
            placeholder={t('administration.noDepartment')}
            options={departments.map((d) => ({ label: d.name, value: d.id }))}
          />
        </Form.Item>
        <Form.Item name="isActive" label={t('administration.isActive')} valuePropName="checked">
          <Switch
            onChange={(checked) => {
              // Disabling a user is a high-impact action (Phase 5.5.2 §25) — confirm it; if the
              // administrator cancels, revert the switch back to on.
              if (checked) return;
              Modal.confirm({
                title: t('administration.disableUserConfirmTitle'),
                icon: <ExclamationCircleOutlined />,
                content: `${user.displayName} ${t('administration.disableUserConfirmSuffix')}`,
                okText: t('administration.disableUser'),
                okButtonProps: { danger: true },
                cancelText: t('common.cancel'),
                onCancel: () => form.setFieldValue('isActive', true),
              });
            }}
          />
        </Form.Item>
      </Form>
    </Modal>
  );
}
