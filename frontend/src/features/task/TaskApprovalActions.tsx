import { useEffect, useState } from 'react';
import { Button, Card, Input, Modal, Select, Space, message } from 'antd';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { useUsersById } from '../../hooks/useUsers';
import { useTranslation } from '../../i18n/LanguageContext';

export type TaskMutation<TVariables> = {
  mutate: (vars: { id: string; variables: TVariables }, opts?: { onSuccess?: () => void; onError?: (err: unknown) => void }) => void;
  isPending: boolean;
  error: unknown;
};

// Full approval action set on Task Detail (Phase 5.4.4 §29) — the existing backend already
// supports Approve/Reject/Return/Delegate/Transfer/AddApprover (Phase 3's ApprovalEngine); Task
// Detail is the correct interaction point for all of them rather than a separate "Approval Engine
// UI" page (this phase's own instruction). No eligibility/allow-flag pre-check is done client-side
// (an approval node's AllowReject/AllowReturn/AllowDelegate/AllowTransfer/AllowAddApprover config
// isn't exposed on TaskDto) — every action is offered and the backend's own
// `*_NOT_ALLOWED`/`APPROVAL_NOT_ASSIGNED`/`ADD_APPROVER_REQUIRES_ADMIN` rejection is surfaced via
// the shared ApiErrorAlert, exactly like every other backend-authoritative action in this app;
// Add Approver is hidden for a non-Administrator purely as a UX nicety, never as the actual
// enforcement (the backend rejects it regardless of what the UI shows).
//
// Phase 13 coupling audit — extracted out of TaskDetailPage.tsx (was a 502-line file mixing task
// state, form-instance resolution, and this entire approval-action block). Pure prop-in/callback-
// out extraction, no behavior change — TaskDetailPage still owns the mutations (from ./hooks) and
// passes them down.
export function ApprovalTaskActions({
  taskId,
  isActionable,
  isAdministrator,
  approveMutation,
  rejectMutation,
  returnMutation,
  delegateMutation,
  transferMutation,
  addApproverMutation,
}: {
  taskId: string;
  isActionable: boolean;
  isAdministrator: boolean;
  approveMutation: TaskMutation<void>;
  rejectMutation: TaskMutation<void>;
  returnMutation: TaskMutation<void>;
  delegateMutation: TaskMutation<{ delegateToUserId: string }>;
  transferMutation: TaskMutation<{ newUserId: string; reason?: string | null }>;
  addApproverMutation: TaskMutation<string>;
}) {
  const { t } = useTranslation();
  const [delegateOpen, setDelegateOpen] = useState(false);
  const [transferOpen, setTransferOpen] = useState(false);
  const [addApproverOpen, setAddApproverOpen] = useState(false);

  const error =
    approveMutation.error ?? rejectMutation.error ?? returnMutation.error ?? delegateMutation.error ?? transferMutation.error ?? addApproverMutation.error;

  return (
    <Card title={t('tasks.approvalCardTitle')}>
      <Space direction="vertical" style={{ width: '100%' }}>
        {error ? <ApiErrorAlert error={error} title={t('tasks.actionFailed')} /> : null}
        <Space wrap>
          <Button
            type="primary"
            disabled={!isActionable}
            loading={approveMutation.isPending}
            onClick={() => approveMutation.mutate({ id: taskId, variables: undefined }, { onSuccess: () => message.success(t('tasks.approvedMsg')) })}
          >
            {t('common.approve')}
          </Button>
          <Button
            danger
            disabled={!isActionable}
            loading={rejectMutation.isPending}
            onClick={() => rejectMutation.mutate({ id: taskId, variables: undefined }, { onSuccess: () => message.success(t('tasks.rejectedMsg')) })}
          >
            {t('common.reject')}
          </Button>
          <Button
            disabled={!isActionable}
            loading={returnMutation.isPending}
            onClick={() => returnMutation.mutate({ id: taskId, variables: undefined }, { onSuccess: () => message.success(t('tasks.returnedMsg')) })}
          >
            {t('common.return')}
          </Button>
          <Button disabled={!isActionable} onClick={() => setDelegateOpen(true)}>
            {t('common.delegate')}
          </Button>
          <Button disabled={!isActionable} onClick={() => setTransferOpen(true)}>
            {t('common.transfer')}
          </Button>
          {isAdministrator && (
            <Button disabled={!isActionable} onClick={() => setAddApproverOpen(true)}>
              {t('tasks.addApprover')}
            </Button>
          )}
        </Space>
      </Space>

      <UserPickerModal
        title={t('tasks.delegateModalTitle')}
        open={delegateOpen}
        confirmLoading={delegateMutation.isPending}
        onCancel={() => setDelegateOpen(false)}
        onConfirm={(userId) =>
          delegateMutation.mutate(
            { id: taskId, variables: { delegateToUserId: userId } },
            { onSuccess: () => { message.success(t('tasks.delegatedMsg')); setDelegateOpen(false); } },
          )
        }
      />
      <UserPickerModal
        title={t('tasks.transferModalTitle')}
        open={transferOpen}
        confirmLoading={transferMutation.isPending}
        withReason
        onCancel={() => setTransferOpen(false)}
        onConfirm={(userId, reason) =>
          transferMutation.mutate(
            { id: taskId, variables: { newUserId: userId, reason } },
            { onSuccess: () => { message.success(t('tasks.transferredMsg')); setTransferOpen(false); } },
          )
        }
      />
      <UserPickerModal
        title={t('tasks.addApproverModalTitle')}
        open={addApproverOpen}
        confirmLoading={addApproverMutation.isPending}
        onCancel={() => setAddApproverOpen(false)}
        onConfirm={(userId) =>
          addApproverMutation.mutate(
            { id: taskId, variables: userId },
            { onSuccess: () => { message.success(t('tasks.approverAddedMsg')); setAddApproverOpen(false); } },
          )
        }
      />
    </Card>
  );
}

function UserPickerModal({
  title,
  open,
  confirmLoading,
  withReason,
  onCancel,
  onConfirm,
}: {
  title: string;
  open: boolean;
  confirmLoading: boolean;
  withReason?: boolean;
  onCancel: () => void;
  onConfirm: (userId: string, reason?: string) => void;
}) {
  const { t } = useTranslation();
  const { users, isLoading } = useUsersById();
  const [userId, setUserId] = useState<string | undefined>(undefined);
  const [reason, setReason] = useState('');

  useEffect(() => {
    if (open) {
      setUserId(undefined);
      setReason('');
    }
  }, [open]);

  return (
    <Modal
      title={title}
      open={open}
      onCancel={onCancel}
      onOk={() => userId && onConfirm(userId, reason || undefined)}
      okButtonProps={{ disabled: !userId }}
      confirmLoading={confirmLoading}
    >
      <Select
        showSearch
        allowClear
        virtual={false}
        style={{ width: '100%' }}
        loading={isLoading}
        placeholder={t('tasks.selectUser')}
        value={userId}
        options={users.map((u) => ({ label: u.displayName, value: u.id }))}
        filterOption={(input, option) => (option?.label ?? '').toLowerCase().includes(input.toLowerCase())}
        onChange={(v) => setUserId(v ?? undefined)}
      />
      {withReason && (
        <Input style={{ marginTop: 12 }} placeholder={t('tasks.reasonPlaceholder')} value={reason} onChange={(e) => setReason(e.target.value)} />
      )}
    </Modal>
  );
}
