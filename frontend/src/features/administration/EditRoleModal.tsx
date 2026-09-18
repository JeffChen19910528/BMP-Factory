import { Alert, Button, Form, Input, Modal } from 'antd';
import { useEffect, useState } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { toApiError } from '../../services/apiClient';
import type { Role } from '../../types/role';
import { useTranslation } from '../../i18n/LanguageContext';
import { useUpdateRole } from './hooks';

const CONCURRENCY_CONFLICT_CODE = 'ROLE_CONCURRENCY_CONFLICT';

interface EditRoleModalProps {
  open: boolean;
  role: Role;
  onClose: () => void;
  onReload: (roleId: string) => Promise<Role | undefined>;
}

// Phase 9 Part 22-26 — rename only. The backend rejects renaming "Administrator"
// (409 CANNOT_RENAME_ADMINISTRATOR_ROLE) — RolesPage.tsx also disables the Edit button for that
// row as a UX hint, but this modal itself makes no assumption that it will only ever be opened
// for a renameable role; the backend's own rejection is what's authoritative either way.
export function EditRoleModal({ open, role, onClose, onReload }: EditRoleModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ name: string }>();
  const mutation = useUpdateRole(role.id);
  const [expectedVersion, setExpectedVersion] = useState(role.rowVersion);
  const [conflicted, setConflicted] = useState(false);

  useEffect(() => {
    if (open) {
      form.setFieldsValue({ name: role.name });
      setExpectedVersion(role.rowVersion);
      setConflicted(false);
      mutation.reset();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, role]);

  function handleOk() {
    form
      .validateFields()
      .then((values) => {
        mutation.mutate(
          { name: values.name, expectedVersion },
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
    const fresh = await onReload(role.id);
    if (fresh) {
      form.setFieldsValue({ name: fresh.name });
      setExpectedVersion(fresh.rowVersion);
      setConflicted(false);
    }
  }

  return (
    <Modal title={t('administration.renameRole')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.save')}>
      {conflicted && (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 16 }}
          message={t('administration.roleConcurrencyConflict')}
          action={
            <Button size="small" danger onClick={handleReload}>
              {t('administration.reload')}
            </Button>
          }
        />
      )}
      {mutation.isError && toApiError(mutation.error).code !== CONCURRENCY_CONFLICT_CODE && (
        <ApiErrorAlert error={mutation.error} title={t('administration.renameRoleError')} />
      )}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item name="name" label={t('common.name')} rules={[{ required: true, message: t('administration.nameRequired') }]}>
          <Input autoFocus />
        </Form.Item>
      </Form>
    </Modal>
  );
}
