import { useState } from 'react';
import { Button, Card, Descriptions, Modal, Space, Tag, message } from 'antd';
import { useNavigate, useParams } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { QueryStateView } from '../../components/QueryStateView';
import { useUsersById } from '../../hooks/useUsers';
import { useTranslation } from '../../i18n/LanguageContext';
import { useAuthStore } from '../../stores/authStore';
import { ApprovalTaskActions } from './TaskApprovalActions';
import { TaskFormRuntime } from './TaskFormRuntime';
import { useAddApprover, useApproveTask, useCompleteTask, useDelegateTask, useRejectTask, useReturnTask, useTask, useTransferTask } from './hooks';
import { useFormInstancesByProcess } from '../form/runtime/hooks';

const statusColors: Record<string, string> = {
  Pending: 'default',
  InProgress: 'blue',
  Completed: 'green',
  Rejected: 'red',
  Returned: 'orange',
  Cancelled: 'default',
  Expired: 'red',
};

// Phase 6.4: 'Overdue' is a real, backend-authoritative status (set by SlaSchedulerWorker) — this
// map only decides its display color, never computes whether a task IS overdue.
const slaStatusColors: Record<string, string> = {
  Active: 'blue',
  Completed: 'green',
  Cancelled: 'default',
  Overdue: 'red',
};

// Task -> Task Detail -> resolve associated Form -> Form Runtime -> Save Draft / Submit ->
// Complete Task (Phase 5.4.3 §13/§21). A UserTask's bound FormInstance is never created here —
// WorkflowTransitions.CreateTaskForNodeAsync already auto-created it alongside the TaskInstance
// when the node has a Form reference (Phase 4), so this page only ever *resolves* it (by matching
// FormInstance.taskInstanceId against this task's id among the process's visible form instances)
// and *loads* it. Task completion for a form-bound UserTask is never called directly from here —
// submitting the FormInstance is what completes the task, server-side, via
// FormEngine.SubmitAsync -> WorkflowTransitions (Phase 4) — this page only ever calls Submit on
// the form.
//
// Phase 13 coupling audit — this file used to also define ApprovalTaskActions/UserPickerModal
// (now TaskApprovalActions.tsx) and TaskFormRuntime (now TaskFormRuntime.tsx). Splitting them out
// was a pure prop-in/callback-out extraction — this page still owns all the mutations (from
// ./hooks) and task/form-instance resolution; the two extracted files only render UI from props
// they're handed. No behavior change, verified by the existing TaskDetailPage.test.tsx (which
// only ever imported TaskDetailPage itself) continuing to pass unmodified.
export function TaskDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { t } = useTranslation();
  const taskQuery = useTask(id);
  const task = taskQuery.data;

  const formInstancesQuery = useFormInstancesByProcess(task?.processInstanceId);
  const boundFormInstance = formInstancesQuery.data?.find((f) => f.taskInstanceId === id);

  const { byId: userNames } = useUsersById();
  const completeMutation = useCompleteTask();
  const approveMutation = useApproveTask();
  const rejectMutation = useRejectTask();
  const returnMutation = useReturnTask();
  const delegateMutation = useDelegateTask();
  const transferMutation = useTransferTask();
  const addApproverMutation = useAddApprover();
  const currentUser = useAuthStore((s) => s.user);
  const isAdministrator = !!currentUser?.roles.includes('Administrator');

  const isActionable = task?.status === 'Pending' || task?.status === 'InProgress';
  const isApprovalTask = !!task?.approval;
  const [formDirty, setFormDirty] = useState(false);

  function guardedBackToTasks() {
    if (!formDirty) {
      navigate('/tasks');
      return;
    }
    Modal.confirm({
      title: t('tasks.unsavedChangesTitle'),
      content: t('tasks.unsavedChangesContent'),
      okText: t('tasks.leave'),
      okButtonProps: { danger: true },
      cancelText: t('tasks.stay'),
      onOk: () => navigate('/tasks'),
    });
  }

  return (
    <QueryStateView isLoading={taskQuery.isLoading} error={taskQuery.error}>
      {task && (
        <div>
          <Card
            title={
              <Space>
                <span>{t('tasks.taskLabel')}: {task.nodeName}</span>
                <Tag color={statusColors[task.status] ?? 'default'}>{task.status}</Tag>
              </Space>
            }
            extra={<Button onClick={guardedBackToTasks}>{t('tasks.backToTasks')}</Button>}
          >
            <Descriptions column={2} bordered size="small">
              <Descriptions.Item label={t('tasks.assignee')}>{task.assigneeId ? userNames.get(task.assigneeId) ?? task.assigneeId : task.assigneeRole ? `${t('tasks.rolePrefix')} ${task.assigneeRole}` : '—'}</Descriptions.Item>
              <Descriptions.Item label={t('common.createdAt')}>{new Date(task.createdAt).toLocaleString()}</Descriptions.Item>
              <Descriptions.Item label={t('tasks.dueAt')}>{task.dueAt ? new Date(task.dueAt).toLocaleString() : '—'}</Descriptions.Item>
              <Descriptions.Item label={t('tasks.completedAt')}>{task.completedAt ? new Date(task.completedAt).toLocaleString() : '—'}</Descriptions.Item>
            </Descriptions>

            {/* Phase 6.3 — SLA Foundation: a simple operational presentation only (Part S) — no
                dashboard, no analytics, no client-computed "Overdue" label (Phase 6.4 owns that
                time-based determination; showing one here would be an unlabeled, persisted-looking
                claim this page never actually persists). Rendered only when this task has an
                applicable SLA at all. */}
            {task.sla && (
              <Descriptions title="SLA" column={2} bordered size="small" style={{ marginTop: 16 }}>
                <Descriptions.Item label={t('common.status')}>
                  <Tag color={slaStatusColors[task.sla.status] ?? 'default'}>{task.sla.status}</Tag>
                </Descriptions.Item>
                <Descriptions.Item label={t('tasks.slaStarted')}>{new Date(task.sla.startedAt).toLocaleString()}</Descriptions.Item>
                <Descriptions.Item label={t('tasks.slaWarningAt')}>{new Date(task.sla.warningAt).toLocaleString()}</Descriptions.Item>
                <Descriptions.Item label={t('tasks.slaDueAt')}>{new Date(task.sla.dueAt).toLocaleString()}</Descriptions.Item>
                <Descriptions.Item label={t('tasks.completedAt')} span={2}>{task.sla.completedAt ? new Date(task.sla.completedAt).toLocaleString() : '—'}</Descriptions.Item>
              </Descriptions>
            )}
          </Card>

          <div style={{ marginTop: 16 }}>
            {isApprovalTask ? (
              <ApprovalTaskActions
                taskId={task.id}
                isActionable={isActionable}
                isAdministrator={isAdministrator}
                approveMutation={approveMutation}
                rejectMutation={rejectMutation}
                returnMutation={returnMutation}
                delegateMutation={delegateMutation}
                transferMutation={transferMutation}
                addApproverMutation={addApproverMutation}
              />
            ) : (
              <QueryStateView isLoading={formInstancesQuery.isLoading} error={formInstancesQuery.error}>
                {boundFormInstance ? (
                  <TaskFormRuntime
                    formInstanceId={boundFormInstance.id}
                    readOnly={!isActionable || boundFormInstance.status !== 'Draft'}
                    onDirtyChange={setFormDirty}
                  />
                ) : (
                  <Card>
                    <Space direction="vertical">
                      <span>{t('tasks.noFormAttached')}</span>
                      {completeMutation.isError && <ApiErrorAlert error={completeMutation.error} title={t('tasks.completeFailed')} />}
                      <Button
                        type="primary"
                        disabled={!isActionable}
                        loading={completeMutation.isPending}
                        onClick={() =>
                          completeMutation.mutate({ id: task.id, variables: undefined }, { onSuccess: () => message.success(t('tasks.taskCompletedMsg')) })
                        }
                      >
                        {t('tasks.completeTask')}
                      </Button>
                    </Space>
                  </Card>
                )}
              </QueryStateView>
            )}
          </div>
        </div>
      )}
    </QueryStateView>
  );
}
