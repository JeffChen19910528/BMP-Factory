import { Alert, Button, Checkbox, Divider, Empty, Input, Select, Space, Typography } from 'antd';
import { DeleteOutlined, PlusOutlined } from '@ant-design/icons';
import { usePublishedFormDefinitions } from '../../../hooks/useFormDefinitions';
import type { ApprovalConfig, ApprovalPolicy, WorkflowAssignment, WorkflowAssignmentType } from '../../../types/process';
import type { WorkflowFlowNode, WorkflowNodeData } from './graphModel';
import { AssignmentValueInput } from './AssignmentValueInput';
import { useTranslation } from '../../../i18n/LanguageContext';

const USER_TASK_ASSIGNMENT_TYPES: WorkflowAssignmentType[] = ['User', 'Role', 'ProcessInitiator'];
const APPROVAL_ASSIGNMENT_TYPES: WorkflowAssignmentType[] = ['User', 'Role', 'Department', 'DepartmentManager', 'ProcessInitiator'];
const APPROVAL_POLICIES: ApprovalPolicy[] = ['Sequential', 'All', 'AnyOne'];

interface PropertiesPanelProps {
  node: WorkflowFlowNode | null;
  readOnly: boolean;
  onChange: (nodeId: string, data: Partial<WorkflowNodeData>) => void;
  onDelete: (nodeId: string) => void;
}

// Only ever shows fields the backend actually supports for the selected node's type — see
// WorkflowDefinitionValidator's SupportedUserTaskAssignmentTypes/SupportedApprovalAssignmentTypes
// for the source of truth on which assignment types are valid where (frontend spec §8: "Only
// expose configuration currently supported by the backend").
export function PropertiesPanel({ node, readOnly, onChange, onDelete }: PropertiesPanelProps) {
  const { t } = useTranslation();

  if (!node) {
    return (
      <div style={{ padding: 24 }}>
        <Empty description={t('processDesigner.selectNodeHint')} />
      </div>
    );
  }

  if (!node.data.supported) {
    return (
      <div style={{ padding: 16 }}>
        <Typography.Title level={5}>{node.data.name}</Typography.Title>
        <Typography.Paragraph type="warning">
          {t('processDesigner.unsupportedNodePrefix')}{node.data.nodeType}{t('processDesigner.unsupportedNodeSuffix')}
        </Typography.Paragraph>
        <Typography.Paragraph>
          <pre style={{ background: '#fafafa', padding: 8, borderRadius: 4, fontSize: 12 }}>
            {JSON.stringify(node.data.raw, null, 2)}
          </pre>
        </Typography.Paragraph>
      </div>
    );
  }

  function update(partial: Partial<WorkflowNodeData>) {
    onChange(node!.id, partial);
  }

  return (
    <div style={{ padding: 16 }}>
      <Space style={{ width: '100%', justifyContent: 'space-between' }}>
        <Typography.Title level={5} style={{ margin: 0 }}>
          {node.data.nodeType}
        </Typography.Title>
        {!readOnly && (
          <Button danger size="small" icon={<DeleteOutlined />} onClick={() => onDelete(node!.id)}>
            {t('common.delete')}
          </Button>
        )}
      </Space>
      <Divider style={{ margin: '12px 0' }} />

      <label>
        <Typography.Text strong>{t('common.name')}</Typography.Text>
        <Input
          value={node.data.name}
          disabled={readOnly}
          onChange={(e) => update({ name: e.target.value })}
          style={{ marginTop: 4 }}
        />
      </label>

      {node.data.nodeType === 'UserTask' && (
        <UserTaskFields node={node} readOnly={readOnly} onChange={update} />
      )}

      {node.data.nodeType === 'ApprovalTask' && (
        <ApprovalTaskFields node={node} readOnly={readOnly} onChange={update} />
      )}
    </div>
  );
}

function UserTaskFields({
  node,
  readOnly,
  onChange,
}: {
  node: WorkflowFlowNode;
  readOnly: boolean;
  onChange: (partial: Partial<WorkflowNodeData>) => void;
}) {
  const assignment = node.data.assignment;
  const { formDefinitions, isLoading: formsLoading, error: formsError } = usePublishedFormDefinitions();
  const { t } = useTranslation();

  return (
    <div style={{ marginTop: 16 }}>
      <Typography.Text strong>{t('processDesigner.assignment')}</Typography.Text>
      <Space.Compact block style={{ marginTop: 4 }}>
        <Select<WorkflowAssignmentType>
          value={assignment?.type ?? 'Role'}
          disabled={readOnly}
          virtual={false}
          style={{ width: '45%' }}
          options={USER_TASK_ASSIGNMENT_TYPES.map((type) => ({ label: type, value: type }))}
          onChange={(type) => onChange({ assignment: { type, value: type === 'ProcessInitiator' ? '' : assignment?.value ?? '' } })}
        />
        <AssignmentValueInput
          type={assignment?.type ?? 'Role'}
          value={assignment?.value ?? ''}
          disabled={readOnly}
          onChange={(value) => onChange({ assignment: { type: assignment?.type ?? 'Role', value } })}
        />
      </Space.Compact>

      <div style={{ marginTop: 16 }}>
        <Typography.Text strong>
          {t('processDesigner.form')} ({t('common.optional')})
        </Typography.Text>
        {/* ApprovalTask deliberately has no Form field: WorkflowTransitions.CreateTaskForNodeAsync
            only reads node.Form inside the UserTask branch — an ApprovalTask's Form would be
            silently ignored by the engine, so the Designer never offers it there (Phase 5.3.2 §6
            finding — see PROGRESS.md). The reference is the FormDefinition's *key*, not a
            FormVersion id: the engine always resolves whichever version is currently published at
            the moment the task is created (WorkflowTransitions/FormEngine.CreateInstanceForTaskAsync),
            the same "pin at creation time, never re-resolve" rule ProcessInstance/ProcessVersion
            follows — so there is no version to pick here, only which form. */}
        {formsError && <Alert type="warning" showIcon message={t('processDesigner.loadFormsError')} style={{ marginBottom: 4 }} />}
        <Select
          allowClear
          showSearch
          virtual={false}
          style={{ width: '100%', marginTop: 4 }}
          disabled={readOnly}
          loading={formsLoading}
          placeholder={t('processDesigner.noForm')}
          value={node.data.form?.formDefinitionKey || undefined}
          options={formDefinitions.map((f) => ({ label: `${f.name} (${f.key})`, value: f.key }))}
          filterOption={(input, option) => (option?.label ?? '').toLowerCase().includes(input.toLowerCase())}
          onChange={(key) => onChange({ form: key ? { formDefinitionKey: key } : null })}
        />
      </div>
    </div>
  );
}

function ApprovalTaskFields({
  node,
  readOnly,
  onChange,
}: {
  node: WorkflowFlowNode;
  readOnly: boolean;
  onChange: (partial: Partial<WorkflowNodeData>) => void;
}) {
  const approval: ApprovalConfig =
    node.data.approval ?? { policy: 'AnyOne', assignments: [], allowReject: true, allowReturn: true, allowDelegate: true, allowTransfer: true, allowAddApprover: true };
  const { t } = useTranslation();

  function updateApproval(partial: Partial<ApprovalConfig>) {
    onChange({ approval: { ...approval, ...partial } });
  }

  function updateAssignment(index: number, next: WorkflowAssignment) {
    const assignments = approval.assignments.slice();
    assignments[index] = next;
    updateApproval({ assignments });
  }

  function removeAssignment(index: number) {
    updateApproval({ assignments: approval.assignments.filter((_, i) => i !== index) });
  }

  function addAssignment() {
    updateApproval({ assignments: [...approval.assignments, { type: 'Role', value: '' }] });
  }

  return (
    <div style={{ marginTop: 16 }}>
      <Typography.Text strong>{t('processDesigner.approvalPolicy')}</Typography.Text>
      <Select<ApprovalPolicy>
        value={approval.policy}
        disabled={readOnly}
        virtual={false}
        style={{ width: '100%', marginTop: 4 }}
        options={APPROVAL_POLICIES.map((p) => ({ label: policyLabel(p, t), value: p }))}
        onChange={(policy) => updateApproval({ policy })}
      />
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {policyDescription(approval.policy, t)}
      </Typography.Text>

      <div style={{ marginTop: 16 }}>
        <Typography.Text strong>{t('processDesigner.assignments')}</Typography.Text>
        <Space direction="vertical" style={{ width: '100%', marginTop: 4 }}>
          {approval.assignments.map((a, index) => (
            <Space.Compact block key={index}>
              <Select<WorkflowAssignmentType>
                value={a.type}
                disabled={readOnly}
                virtual={false}
                style={{ width: '40%' }}
                options={APPROVAL_ASSIGNMENT_TYPES.map((type) => ({ label: type, value: type }))}
                onChange={(type) => updateAssignment(index, { type, value: type === 'ProcessInitiator' ? '' : a.value })}
              />
              <AssignmentValueInput
                type={a.type}
                value={a.value}
                disabled={readOnly}
                onChange={(value) => updateAssignment(index, { type: a.type, value })}
              />
              {!readOnly && (
                <Button icon={<DeleteOutlined />} onClick={() => removeAssignment(index)} />
              )}
            </Space.Compact>
          ))}
          {!readOnly && (
            <Button block icon={<PlusOutlined />} onClick={addAssignment}>
              {t('processDesigner.addAssignment')}
            </Button>
          )}
          {approval.assignments.length === 0 && (
            <Typography.Text type="danger" style={{ fontSize: 12 }}>
              {t('processDesigner.assignmentRequired')}
            </Typography.Text>
          )}
        </Space>
      </div>

      <div style={{ marginTop: 16 }}>
        <Typography.Text strong>{t('processDesigner.actionsAllowed')}</Typography.Text>
        <Typography.Paragraph type="secondary" style={{ fontSize: 12, marginBottom: 4 }}>
          {t('processDesigner.actionsAllowedHint')}
        </Typography.Paragraph>
        <div style={{ marginTop: 4, display: 'flex', flexDirection: 'column', gap: 4 }}>
          <Checkbox
            disabled={readOnly}
            checked={approval.allowReject ?? true}
            onChange={(e) => updateApproval({ allowReject: e.target.checked })}
          >
            {t('common.reject')} — {t('processDesigner.allowRejectDescription')}
          </Checkbox>
          <Checkbox
            disabled={readOnly}
            checked={approval.allowReturn ?? true}
            onChange={(e) => updateApproval({ allowReturn: e.target.checked })}
          >
            {t('common.return')} — {t('processDesigner.allowReturnDescription')}
          </Checkbox>
          <Checkbox
            disabled={readOnly}
            checked={approval.allowDelegate ?? true}
            onChange={(e) => updateApproval({ allowDelegate: e.target.checked })}
          >
            {t('common.delegate')} — {t('processDesigner.allowDelegateDescription')}
          </Checkbox>
          <Checkbox
            disabled={readOnly}
            checked={approval.allowTransfer ?? true}
            onChange={(e) => updateApproval({ allowTransfer: e.target.checked })}
          >
            {t('common.transfer')} — {t('processDesigner.allowTransferDescription')}
          </Checkbox>
          <Checkbox
            disabled={readOnly}
            checked={approval.allowAddApprover ?? true}
            onChange={(e) => updateApproval({ allowAddApprover: e.target.checked })}
          >
            {t('processDesigner.addApprover')} {t('processDesigner.addApproverDescription')}
          </Checkbox>
        </div>
      </div>
    </div>
  );
}

function policyLabel(policy: ApprovalPolicy, t: (path: string) => string): string {
  switch (policy) {
    case 'AnyOne':
      return `AnyOne — ${t('processDesigner.policyAnyOneLabel')}`;
    case 'All':
      return `All — ${t('processDesigner.policyAllLabel')}`;
    case 'Sequential':
      return `Sequential — ${t('processDesigner.policySequentialLabel')}`;
  }
}

function policyDescription(policy: ApprovalPolicy, t: (path: string) => string): string {
  switch (policy) {
    case 'AnyOne':
      return t('processDesigner.policyAnyOneDescription');
    case 'All':
      return t('processDesigner.policyAllDescription');
    case 'Sequential':
      return t('processDesigner.policySequentialDescription');
  }
}
