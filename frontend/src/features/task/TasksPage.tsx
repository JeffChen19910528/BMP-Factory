import { Button, Space, Table, Tag } from 'antd';
import type { TableProps } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import { useNavigate } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { useUsersById } from '../../hooks/useUsers';
import type { Task, TaskInstanceStatus } from '../../types/task';
import { useTranslation } from '../../i18n/LanguageContext';
import { useMyTasks } from './hooks';

const statusColors: Record<TaskInstanceStatus, string> = {
  Pending: 'default',
  InProgress: 'blue',
  Completed: 'green',
  Rejected: 'red',
  Returned: 'orange',
  Cancelled: 'default',
  Expired: 'red',
};

// Phase 5.4.3 §22: the previously-placeholder My Tasks page, now real — task/process step/
// assignee/status/created time/due date, enough to identify and open a task. SLA-specific
// functionality (escalation, overdue highlighting beyond just showing dueAt) is explicitly out of
// scope this phase.
export function TasksPage() {
  const navigate = useNavigate();
  const query = useMyTasks();
  const { byId: userNames } = useUsersById();
  const { t } = useTranslation();

  const columns: TableProps<Task>['columns'] = [
    { title: t('tasks.node'), dataIndex: 'nodeName', key: 'nodeName' },
    {
      title: t('tasks.assignee'),
      key: 'assignee',
      render: (_, record) => (record.assigneeId ? userNames.get(record.assigneeId) ?? record.assigneeId : record.assigneeRole ? `Role: ${record.assigneeRole}` : '—'),
    },
    {
      title: t('common.status'),
      dataIndex: 'status',
      key: 'status',
      render: (s: TaskInstanceStatus) => <Tag color={statusColors[s]}>{s}</Tag>,
    },
    {
      // Phase 5.4.4 §30: enough to tell, before opening it, whether a task is a plain UserTask
      // (Complete Task), an ApprovalTask (with its live approval progress), or a form-bound
      // UserTask (Form Runtime) — the same three branches Task Detail itself renders.
      title: t('tasks.approvalColumn'),
      key: 'approval',
      render: (_, record) =>
        record.approval ? (
          <Tag color="purple">
            {record.approval.policy} {record.approval.approvedCount}/{record.approval.requiredCount}
          </Tag>
        ) : (
          '—'
        ),
    },
    { title: t('common.createdAt'), dataIndex: 'createdAt', key: 'createdAt', render: (v: string) => new Date(v).toLocaleString() },
    { title: t('tasks.dueAt'), dataIndex: 'dueAt', key: 'dueAt', render: (v: string | null) => (v ? new Date(v).toLocaleString() : '—') },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => (
        <Button size="small" onClick={() => navigate(`/tasks/${record.id}`)}>
          {t('tasks.openTask')}
        </Button>
      ),
    },
  ];

  return (
    <div>
      <Space style={{ marginBottom: 16, width: '100%', justifyContent: 'space-between' }}>
        <Button icon={<ReloadOutlined />} onClick={() => query.refetch()} loading={query.isFetching}>
          {t('dashboard.refresh')}
        </Button>
      </Space>

      {query.isError ? (
        <ApiErrorAlert error={query.error} title={t('tasks.title')} />
      ) : (
        <Table<Task>
          rowKey="id"
          columns={columns}
          dataSource={query.data ?? []}
          loading={query.isLoading}
          locale={{ emptyText: t('tasks.empty') }}
        />
      )}
    </div>
  );
}
