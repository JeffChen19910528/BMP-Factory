import { Alert, Button, Form, Input, Modal } from 'antd';
import { useEffect, useState } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { toApiError } from '../../services/apiClient';
import type { User } from '../../types/user';
import { useTranslation } from '../../i18n/LanguageContext';
import { useResetUserPassword } from './hooks';

const CONCURRENCY_CONFLICT_CODE = 'USER_CONCURRENCY_CONFLICT';

interface ResetPasswordModalProps {
  open: boolean;
  user: User;
  onClose: () => void;
  onReload: (userId: string) => Promise<User | undefined>;
}

// Phase 9 Part 14-16 — Administrator-operated direct password reset (no email/token workflow).
// The new password is never displayed or logged after submission, on success or failure — the
// form field is simply unmounted when the modal closes.
export function ResetPasswordModal({ open, user, onClose, onReload }: ResetPasswordModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ newPassword: string; confirmPassword: string }>();
  const mutation = useResetUserPassword(user.id);
  const [expectedVersion, setExpectedVersion] = useState(user.rowVersion);
  const [conflicted, setConflicted] = useState(false);

  useEffect(() => {
    if (open) {
      form.resetFields();
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
          { newPassword: values.newPassword, expectedVersion },
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
      setExpectedVersion(fresh.rowVersion);
      setConflicted(false);
    }
  }

  return (
    <Modal title={`${t('common.resetPassword')} — ${user.displayName}`} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.resetPassword')} okButtonProps={{ danger: true }}>
      {conflicted && (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 16 }}
          message={t('administration.resetPasswordConflict')}
          action={
            <Button size="small" danger onClick={handleReload}>
              {t('administration.reload')}
            </Button>
          }
        />
      )}
      {mutation.isError && toApiError(mutation.error).code !== CONCURRENCY_CONFLICT_CODE && (
        <ApiErrorAlert error={mutation.error} title={t('administration.resetPasswordError')} />
      )}
      <Alert type="warning" showIcon style={{ marginBottom: 16 }} message={`${user.username} ${t('administration.resetPasswordWarningSuffix')}`} />
      <Form form={form} layout="vertical">
        <Form.Item
          name="newPassword"
          label={t('administration.newPassword')}
          rules={[
            { required: true, message: t('administration.newPasswordRequired') },
            { min: 8, message: t('administration.passwordMinLength') },
          ]}
        >
          <Input.Password autoFocus autoComplete="new-password" />
        </Form.Item>
        <Form.Item
          name="confirmPassword"
          label={t('administration.confirmPassword')}
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
    </Modal>
  );
}
