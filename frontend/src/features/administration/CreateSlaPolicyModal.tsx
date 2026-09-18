import { Form, InputNumber, Modal, Select, Switch } from 'antd';
import { useEffect, useMemo, useState } from 'react';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { useProcessDefinitions, useProcessVersions } from '../process/hooks';
import { useTranslation } from '../../i18n/LanguageContext';
import { useCreateSlaPolicy } from './hooks';

interface CreateSlaPolicyModalProps {
  open: boolean;
  onClose: () => void;
}

// Phase 9 Part 1-3 — SlaPolicy is scoped by (ProcessDefinitionId, NodeId); NodeId is a string
// matching a node inside that process's own WorkflowDefinition JSON, never a separate entity (see
// SlaPolicy.cs's own doc comment) — so this picker derives its Node options from the selected
// process's own versions' definitions rather than a new backend endpoint. Falls back gracefully
// (Select still usable with a typed value) if a process has no versions yet.
export function CreateSlaPolicyModal({ open, onClose }: CreateSlaPolicyModalProps) {
  const { t } = useTranslation();
  const [form] = Form.useForm<{ processDefinitionId: string; nodeId: string; enabled: boolean; durationMinutes: number; warningOffsetMinutes: number }>();
  const mutation = useCreateSlaPolicy();
  const [selectedProcessId, setSelectedProcessId] = useState<string | undefined>(undefined);

  const processDefinitionsQuery = useProcessDefinitions({ page: 1, pageSize: 200 });
  const processDefinitions = processDefinitionsQuery.data?.items ?? [];
  const versionsQuery = useProcessVersions(selectedProcessId);

  const nodeOptions = useMemo(() => {
    const versions = versionsQuery.data ?? [];
    const seen = new Map<string, string>();
    for (const version of versions) {
      for (const node of version.definition.nodes) {
        if (node.type === 'UserTask' || node.type === 'ApprovalTask') {
          seen.set(node.id, node.name);
        }
      }
    }
    return Array.from(seen.entries()).map(([id, name]) => ({ value: id, label: `${name} (${id})` }));
  }, [versionsQuery.data]);

  useEffect(() => {
    if (open) {
      form.resetFields();
      setSelectedProcessId(undefined);
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
            processDefinitionId: values.processDefinitionId,
            nodeId: values.nodeId,
            enabled: values.enabled ?? true,
            durationMinutes: values.durationMinutes,
            warningOffsetMinutes: values.warningOffsetMinutes,
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
    <Modal title={t('administration.createSlaPolicy')} open={open} onCancel={onClose} onOk={handleOk} confirmLoading={mutation.isPending} okText={t('common.create')}>
      {mutation.isError && <ApiErrorAlert error={mutation.error} title={t('administration.createSlaPolicyError')} />}
      <Form form={form} layout="vertical" style={{ marginTop: 16 }} initialValues={{ enabled: true }}>
        <Form.Item name="processDefinitionId" label={t('administration.process')} rules={[{ required: true, message: t('administration.processRequired') }]}>
          <Select
            showSearch
            optionFilterProp="label"
            options={processDefinitions.map((p) => ({ label: p.name, value: p.id }))}
            onChange={(value) => {
              setSelectedProcessId(value);
              form.setFieldValue('nodeId', undefined);
            }}
          />
        </Form.Item>
        <Form.Item name="nodeId" label={t('administration.node')} rules={[{ required: true, message: t('administration.nodeRequired') }]}>
          <Select
            showSearch
            optionFilterProp="label"
            disabled={!selectedProcessId}
            placeholder={selectedProcessId ? t('administration.selectNodePlaceholder') : t('administration.selectProcessFirstPlaceholder')}
            options={nodeOptions}
          />
        </Form.Item>
        <Form.Item name="enabled" label={t('administration.enabled')} valuePropName="checked">
          <Switch />
        </Form.Item>
        <Form.Item name="durationMinutes" label={t('administration.durationMinutes')} rules={[{ required: true, message: t('administration.durationRequired') }]}>
          <InputNumber min={1} style={{ width: '100%' }} />
        </Form.Item>
        <Form.Item
          name="warningOffsetMinutes"
          label={t('administration.warningOffsetMinutes')}
          rules={[{ required: true, message: t('administration.warningOffsetRequired') }]}
          extra={t('administration.warningOffsetExtra')}
        >
          <InputNumber min={0} style={{ width: '100%' }} />
        </Form.Item>
      </Form>
    </Modal>
  );
}
