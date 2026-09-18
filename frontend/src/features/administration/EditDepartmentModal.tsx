import { Alert, Button, Form, Input, Modal, Select } from 'antd';
import { useEffect, useState } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { toApiError } from '../../services/apiClient';
import type { Department } from '../../types/department';
import type { User } from '../../types/user';
import { useTranslation } from '../../i18n/LanguageContext';
import { useUpdateDepartment } from './hooks';

const CONCURRENCY_CONFLICT_CODE = 'DEPARTMENT_CONCURRENCY_CONFLICT';

interface EditDepartmentModalProps {
  open: boolean;
  department: Department;
  departments: Department[];
  users: User[];
  onClose: () => void;
  onReload: (departmentId: string) => Promise<Department | undefined>;
}

export function EditDepartmentModal({ open, department, departments, users, onClose, onReload }: EditDepartmentModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ name: string; parentId?: string; managerUserId?: string }>();
  const mutation = useUpdateDepartment(department.id);
  const [expectedVersion, setExpectedVersion] = useState(department.rowVersion);
  const [conflicted, setConflicted] = useState(false);

  useEffect(() => {
    if (open) {
      form.setFieldsValue({
        name: department.name,
        parentId: department.parentId ?? undefined,
        managerUserId: department.managerUserId ?? undefined,
      });
      setExpectedVersion(department.rowVersion);
      setConflicted(false);
      mutation.reset();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, department]);

  function submit(values: { name: string; parentId?: string; managerUserId?: string }) {
    mutation.mutate(
      {
        name: values.name,
        parentId: values.parentId ?? null,
        managerUserId: values.managerUserId ?? null,
        expectedVersion,
      },
      {
        onSuccess: () => onClose(),
        onError: (error) => {
          if (toApiError(error).code === CONCURRENCY_CONFLICT_CODE) setConflicted(true);
        },
      },
    );
  }

  function handleOk() {
    form
      .validateFields()
      .then((values) => {
        const managerChanged = (values.managerUserId ?? null) !== department.managerUserId;
        if (managerChanged) {
          // Changing a department's manager is a high-impact action (Phase 5.5.2 §25).
          Modal.confirm({
            title: t('administration.changeManagerConfirmTitle'),
            content: t('administration.changeManagerConfirmContent'),
            okText: t('administration.changeManager'),
            onOk: () => submit(values),
          });
        } else {
          submit(values);
        }
      })
      .catch(() => {
        // AntD rejects validateFields() on a failed client-side rule — the form already
        // renders the field errors, so there's nothing further to do here.
      });
  }

  async function handleReload() {
    const fresh = await onReload(department.id);
    if (fresh) {
      form.setFieldsValue({
        name: fresh.name,
        parentId: fresh.parentId ?? undefined,
        managerUserId: fresh.managerUserId ?? undefined,
      });
      setExpectedVersion(fresh.rowVersion);
      setConflicted(false);
    }
  }

  // A department cannot be re-parented to itself or to any of its own descendants — the backend
  // is authoritative (walks the real ancestor chain, catching CIRCULAR_DEPARTMENT_HIERARCHY at
  // any depth), this is just an obvious-case narrowing of the picker so the common mistake isn't
  // even offered, not a re-implementation of that validation in React.
  const parentOptions = departments.filter((d) => d.id !== department.id);

  return (
    <Modal title={t('administration.editDepartment')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.save')}>
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
        <ApiErrorAlert error={mutation.error} title={t('administration.updateDepartmentError')} />
      )}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item name="name" label={t('common.name')} rules={[{ required: true, message: t('administration.nameRequired') }]}>
          <Input autoFocus />
        </Form.Item>
        <Form.Item name="parentId" label={t('administration.parentDepartment')}>
          <Select allowClear placeholder={t('administration.noParentTopLevel')} options={parentOptions.map((d) => ({ label: d.name, value: d.id }))} />
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
