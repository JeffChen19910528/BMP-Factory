import { Alert, Form, Input, Modal } from 'antd';
import { useEffect, useState } from 'react';
import { ApiErrorAlert } from './ApiErrorAlert';
import { useChangeMyPassword } from '../features/administration/hooks';
import { useTranslation } from '../i18n/LanguageContext';

interface ChangePasswordModalProps {
  open: boolean;
  onClose: () => void;
}

// Phase 9 Part 17-19 — self-service change password (POST /api/users/me/change-password). The
// caller's identity comes from the JWT alone (ICurrentUserService server-side) — this form never
// sends a user id. New/current password values are never logged or displayed after submission.
export function ChangePasswordModal({ open, onClose }: ChangePasswordModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ currentPassword: string; newPassword: string; confirmPassword: string }>();
  const mutation = useChangeMyPassword();
  const [succeeded, setSucceeded] = useState(false);

  useEffect(() => {
    if (open) {
      form.resetFields();
      setSucceeded(false);
      mutation.reset();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  function handleOk() {
    if (succeeded) {
      onClose();
      return;
    }
    form
      .validateFields()
      .then((values) => {
        mutation.mutate(
          { currentPassword: values.currentPassword, newPassword: values.newPassword },
          { onSuccess: () => setSucceeded(true) },
        );
      })
      .catch(() => {
        // AntD rejects validateFields() on a failed client-side rule — the form already
        // renders the field errors, so there's nothing further to do here.
      });
  }

  return (
    <Modal
      title={t('common.changePassword')}
      open={open}
      onCancel={onClose}
      onOk={handleOk}
      confirmLoading={mutation.isPending}
      okText={succeeded ? t('common.close') : t('common.changePassword')}
      cancelButtonProps={succeeded ? { style: { display: 'none' } } : undefined}
    >
      {succeeded ? (
        <Alert type="success" showIcon message={t('administration.passwordChangedSuccess')} />
      ) : (
        <>
          {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('administration.changePasswordError')} />}
          <Form form={form} layout="vertical">
            <Form.Item
              name="currentPassword"
              label={t('administration.currentPassword')}
              rules={[{ required: true, message: t('administration.currentPasswordRequired') }]}
            >
              <Input.Password autoFocus autoComplete="current-password" />
            </Form.Item>
            <Form.Item
              name="newPassword"
              label={t('administration.newPassword')}
              rules={[
                { required: true, message: t('administration.newPasswordRequired') },
                { min: 8, message: t('administration.passwordMinLength') },
              ]}
            >
              <Input.Password autoComplete="new-password" />
            </Form.Item>
            <Form.Item
              name="confirmPassword"
              label={t('administration.confirmNewPassword')}
              dependencies={['newPassword']}
              rules={[
                { required: true, message: t('administration.confirmPasswordRequired') },
                ({ getFieldValue }) => ({
                  validator(_, value) {
                    if (!value || getFieldValue('newPassword') === value) return Promise.resolve();
                    return Promise.reject(new Error(t('administration.passwordMismatch')));
                  },
                }),
              ]}
            >
              <Input.Password autoComplete="new-password" />
            </Form.Item>
          </Form>
        </>
      )}
    </Modal>
  );
}
