import { Alert, Button, Form, InputNumber, Modal, Switch } from 'antd';
import { useEffect, useState } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { toApiError } from '../../services/apiClient';
import type { SlaPolicy } from '../../types/slaPolicy';
import { useTranslation } from '../../i18n/LanguageContext';
import { useUpdateSlaPolicy } from './hooks';

const CONCURRENCY_CONFLICT_CODE = 'SLA_POLICY_CONCURRENCY_CONFLICT';

interface EditSlaPolicyModalProps {
  open: boolean;
  policy: SlaPolicy;
  onClose: () => void;
  onReload: (policyId: string) => Promise<SlaPolicy | undefined>;
}

// Process and Node are immutable once a policy exists (the unique (ProcessDefinitionId, NodeId)
// pair is what CreateAsync's own duplicate check keys off) — only Enabled/Duration/WarningOffset
// are editable here, matching UpdateSlaPolicyRequest's own shape exactly.
export function EditSlaPolicyModal({ open, policy, onClose, onReload }: EditSlaPolicyModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ enabled: boolean; durationMinutes: number; warningOffsetMinutes: number }>();
  const mutation = useUpdateSlaPolicy(policy.id);
  const [expectedVersion, setExpectedVersion] = useState(policy.rowVersion);
  const [conflicted, setConflicted] = useState(false);

  useEffect(() => {
    if (open) {
      form.setFieldsValue({ enabled: policy.enabled, durationMinutes: policy.durationMinutes, warningOffsetMinutes: policy.warningOffsetMinutes });
      setExpectedVersion(policy.rowVersion);
      setConflicted(false);
      mutation.reset();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, policy]);

  function handleOk() {
    form
      .validateFields()
      .then((values) => {
        mutation.mutate(
          { enabled: values.enabled, durationMinutes: values.durationMinutes, warningOffsetMinutes: values.warningOffsetMinutes, expectedVersion },
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
    const fresh = await onReload(policy.id);
    if (fresh) {
      form.setFieldsValue({ enabled: fresh.enabled, durationMinutes: fresh.durationMinutes, warningOffsetMinutes: fresh.warningOffsetMinutes });
      setExpectedVersion(fresh.rowVersion);
      setConflicted(false);
    }
  }

  return (
    <Modal title={t('administration.editSlaPolicy')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.save')}>
      {conflicted && (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 16 }}
          message={t('administration.slaPolicyConcurrencyConflict')}
          action={
            <Button size="small" danger onClick={handleReload}>
              {t('administration.reload')}
            </Button>
          }
        />
      )}
      {mutation.isError && toApiError(mutation.error).code !== CONCURRENCY_CONFLICT_CODE && (
        <ApiErrorAlert error={mutation.error} title={t('administration.updateSlaPolicyError')} />
      )}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
        <Form.Item name="enabled" label={t('administration.enabled')} valuePropName="checked" extra={t('administration.slaEnabledExtra')}>
          <Switch />
        </Form.Item>
        <Form.Item name="durationMinutes" label={t('administration.durationMinutes')} rules={[{ required: true, message: t('administration.durationRequired') }]}>
          <InputNumber min={1} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item
          name="warningOffsetMinutes"
          label={t('administration.warningOffsetMinutes')}
          rules={[{ required: true, message: t('administration.warningOffsetRequired') }]}
          extra={t('administration.warningOffsetExtraShort')}
        >
          <InputNumber min={0} style={{ width: '100%' }} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
