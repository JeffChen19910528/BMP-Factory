import { Alert, Card, Descriptions, Empty, Skeleton, Space, Steps, Table, Tag, Timeline, Typography } from 'antd';
import type { TableProps } from 'antd';
import { useNavigate, useParams } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { useUsersById } from '../../hooks/useUsers';
import type { ProcessInstanceStatus } from '../../types/process';
import type { ProcessMonitoringSlaStatus, ProcessProgressState, ProcessTimelineItem } from '../../types/processMonitoring';
import type { Task, TaskInstanceStatus, TaskSlaStatus } from '../../types/task';
import { useTranslation } from '../../i18n/LanguageContext';
import { useProcessInstanceDetail } from './hooks';

const statusColors: Record<ProcessInstanceStatus, string> = {
  Running: 'blue',
  Completed: 'green',
  Rejected: 'red',
  Cancelled: 'default',
  Suspended: 'orange',
  Failed: 'red',
};

const taskStatusColors: Record<TaskInstanceStatus, string> = {
  Pending: 'default',
  InProgress: 'blue',
  Completed: 'green',
  Rejected: 'red',
  Returned: 'orange',
  Cancelled: 'default',
  Expired: 'red',
};

// Per-task rows (Task History) show the raw TaskSlaStatus — same convention as Task Detail.
const slaStatusColors: Record<TaskSlaStatus, string> = {
  Active: 'blue',
  Completed: 'green',
  Cancelled: 'default',
  Overdue: 'red',
};

// The Current Task summary shows the *derived* bucket (adds Warning) — same colors/values
// Process Monitoring's list already uses, so the two never visually disagree for the same task.
const summarySlaStatusColors: Record<ProcessMonitoringSlaStatus, string> = {
  Active: 'blue',
  Warning: 'gold',
  Overdue: 'red',
  Completed: 'green',
};

const progressStepStatus: Record<ProcessProgressState, 'finish' | 'process' | 'wait'> = {
  Completed: 'finish',
  Current: 'process',
  Pending: 'wait',
};

function taskAssigneeDisplay(task: Task, userNames: Map<string, string>): string {
  if (task.assigneeId) {
    return userNames.get(task.assigneeId) ?? task.assigneeId;
  }
  if (task.assigneeRole) {
    return `Role: ${task.assigneeRole}`;
  }
  if (task.approval) {
    return `${task.approval.policy} ${task.approval.approvedCount}/${task.approval.requiredCount}`;
  }
  return '—';
}

// Phase 7.2.2 — Process Instance Detail + Timeline, read-only. Every value here comes directly
// from GET /api/process-monitoring/{id} — this page computes nothing (no client-side Overdue/
// progress-state derivation anywhere in this feature; see CurrentTask/WorkflowProgress/SlaSummary,
// all backend-authoritative). A 403/404 from the backend renders through the existing
// ApiErrorAlert, exactly as any other page's error state — never silently redirected away from.
export function ProcessInstanceDetailPage() {
  const { processInstanceId } = useParams<{ processInstanceId: string }>();
  const navigate = useNavigate();
  const { t } = useTranslation();
  const query = useProcessInstanceDetail(processInstanceId);
  const { byId: userNames } = useUsersById();

  if (query.isLoading) {
    return <Skeleton active />;
  }

  if (query.isError) {
    return <ApiErrorAlert error={query.error} title="Failed to load process instance" />;
  }

  const detail = query.data;
  if (!detail) {
    return <Empty description="Process instance not found." />;
  }

  const taskColumns: TableProps<Task>['columns'] = [
    { title: 'Task', dataIndex: 'nodeName', key: 'nodeName' },
    {
      title: 'Status',
      dataIndex: 'status',
      key: 'status',
      render: (s: TaskInstanceStatus) => <Tag color={taskStatusColors[s]}>{s}</Tag>,
    },
    { title: 'Assignee', key: 'assignee', render: (_, record) => taskAssigneeDisplay(record, userNames) },
    { title: 'Started', dataIndex: 'startedAt', key: 'startedAt', render: (v: string | null) => (v ? new Date(v).toLocaleString() : '—') },
    { title: 'Completed', dataIndex: 'completedAt', key: 'completedAt', render: (v: string | null) => (v ? new Date(v).toLocaleString() : '—') },
    {
      title: 'SLA',
      key: 'sla',
      render: (_, record) => (record.sla ? <Tag color={slaStatusColors[record.sla.status]}>{record.sla.status}</Tag> : '—'),
    },
  ];

  return (
    <div>
      <Space style={{ marginBottom: 16, width: '100%', justifyContent: 'space-between' }}>
        <Typography.Title level={3} style={{ margin: 0 }}>
          {detail.processDefinitionName}
        </Typography.Title>
        <a onClick={() => navigate('/instances')}>Back to Process Monitoring</a>
      </Space>

      <Card title={t("monitoring.detailTitle")}>
        <Descriptions column={2} bordered size="small">
          <Descriptions.Item label="Process">{detail.processDefinitionName}</Descriptions.Item>
          <Descriptions.Item label="Key">{detail.processDefinitionKey}</Descriptions.Item>
          <Descriptions.Item label="Instance ID">
            <Typography.Text code>{detail.processInstanceId}</Typography.Text>
          </Descriptions.Item>
          <Descriptions.Item label="Status">
            <Tag color={statusColors[detail.status]}>{detail.status}</Tag>
          </Descriptions.Item>
          <Descriptions.Item label="Initiator">{detail.initiatorDisplayName}</Descriptions.Item>
          <Descriptions.Item label="Started">{new Date(detail.startedAt).toLocaleString()}</Descriptions.Item>
          <Descriptions.Item label="Completed" span={2}>
            {detail.completedAt ? new Date(detail.completedAt).toLocaleString() : '—'}
          </Descriptions.Item>
        </Descriptions>
      </Card>

      {detail.workflowProgress.length > 0 && (
        <Card title={t("monitoring.workflowProgress")} style={{ marginTop: 16 }}>
          <Steps
            size="small"
            items={detail.workflowProgress.map((p) => ({ title: p.displayName, status: progressStepStatus[p.state] }))}
          />
        </Card>
      )}

      <Card title={t("monitoring.currentTask")} style={{ marginTop: 16 }}>
        {detail.activeTaskCount > 1 ? (
          // Part 6: a genuinely unsupported condition today (the engine is strictly sequential) —
          // never silently pick one of several active tasks.
          <Alert type="warning" showIcon message={`${detail.activeTaskCount} active tasks were found — this is not supported by the current display.`} />
        ) : detail.currentTask ? (
          <Descriptions column={2} bordered size="small">
            <Descriptions.Item label="Task">{detail.currentTask.nodeName}</Descriptions.Item>
            <Descriptions.Item label="Status">
              <Tag color={taskStatusColors[detail.currentTask.status]}>{detail.currentTask.status}</Tag>
            </Descriptions.Item>
            <Descriptions.Item label="Assignee">{taskAssigneeDisplay(detail.currentTask, userNames)}</Descriptions.Item>
            <Descriptions.Item label="SLA">
              {detail.slaStatus ? <Tag color={summarySlaStatusColors[detail.slaStatus]}>{detail.slaStatus}</Tag> : '—'}
            </Descriptions.Item>
            {detail.slaSummary && (
              <Descriptions.Item label="Due" span={2}>
                {new Date(detail.slaSummary.dueAt).toLocaleString()}
              </Descriptions.Item>
            )}
          </Descriptions>
        ) : (
          <Empty description="No active task — the process has reached a terminal state." />
        )}
      </Card>

      <Card title={t("monitoring.taskHistory")} style={{ marginTop: 16 }}>
        <Table<Task> rowKey="id" columns={taskColumns} dataSource={detail.tasks} pagination={false} locale={{ emptyText: 'No tasks yet.' }} />
      </Card>

      <Card title={t("monitoring.timeline")} style={{ marginTop: 16 }}>
        {detail.timeline.length === 0 ? (
          <Empty description="No activity yet." />
        ) : (
          <Timeline
            items={detail.timeline.map((item: ProcessTimelineItem) => ({
              key: `${item.timestamp}-${item.eventType}-${item.sourceId ?? ''}`,
              children: (
                <div>
                  <Typography.Text strong>{item.title}</Typography.Text>
                  <br />
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {new Date(item.timestamp).toLocaleString()}
                    {item.actorDisplayName ? ` · ${item.actorDisplayName}` : ''}
                  </Typography.Text>
                </div>
              ),
            }))}
          />
        )}
      </Card>
    </div>
  );
}
