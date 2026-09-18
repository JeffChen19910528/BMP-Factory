import { ReloadOutlined } from '@ant-design/icons';
import { Button, Input, Segmented, Space, Table, Tag } from 'antd';
import type { TableProps } from 'antd';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { useUsersById } from '../../hooks/useUsers';
import { useTranslation } from '../../i18n/LanguageContext';
import { useApprovalWorklist } from '../task/hooks';
import type { ApprovalWorklistItem, TaskInstanceStatus } from '../../types/task';

type StatusTab = 'Pending' | 'Returned' | 'Completed' | 'All';

const statusColors: Record<TaskInstanceStatus, string> = {
  Pending: 'default',
  InProgress: 'blue',
  Completed: 'green',
  Rejected: 'red',
  Returned: 'orange',
  Cancelled: 'default',
  Expired: 'red',
};

function isStatusTab(value: string | null): value is StatusTab {
  return value === 'Pending' || value === 'Returned' || value === 'Completed' || value === 'All';
}

// Phase 5.5.1 — Approvals Worklist / Operational Approval Center. A dedicated, filterable,
// searchable, paginated view over exactly the approval work the current user participates in —
// built entirely on the new read-only GET /api/tasks/approvals query (Phase 3's Approval Engine
// itself is untouched: every action below still goes through the existing
// approve/reject/return/delegate/transfer/approvers endpoints via Task Detail). Opening an item
// navigates to the existing Task Detail page — there is no second "Approval Detail" page and no
// second approval-progress calculation (the Approval column here uses the exact same
// `{policy} {approvedCount}/{requiredCount}` projection My Tasks already renders, both sourced
// from TaskDtoMapper.BuildApprovalSummary on the backend).
export function ApprovalsPage() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const { byId: userNames } = useUsersById();
  const { t } = useTranslation();

  // Filters live in the URL (§22: refresh/bookmark/back-forward all work) rather than in
  // component state that would reset on navigation.
  const status = isStatusTab(searchParams.get('status')) ? (searchParams.get('status') as StatusTab) : 'Pending';
  const search = searchParams.get('search') ?? '';
  const page = Number(searchParams.get('page') ?? '1') || 1;
  const pageSize = Number(searchParams.get('pageSize') ?? '20') || 20;

  function updateParams(next: { status?: StatusTab; search?: string; page?: number; pageSize?: number }) {
    const params = new URLSearchParams(searchParams);
    if (next.status !== undefined) params.set('status', next.status);
    if (next.search !== undefined) {
      if (next.search) params.set('search', next.search);
      else params.delete('search');
    }
    if (next.page !== undefined) params.set('page', String(next.page));
    if (next.pageSize !== undefined) params.set('pageSize', String(next.pageSize));
    setSearchParams(params);
  }

  const query = useApprovalWorklist({
    status: status === 'All' ? undefined : status,
    search: search || undefined,
    page,
    pageSize,
  });

  const columns: TableProps<ApprovalWorklistItem>['columns'] = [
    { title: t('approvals.process'), key: 'process', render: (_, record) => record.processDefinitionName },
    { title: t('approvals.task'), dataIndex: 'taskName', key: 'taskName' },
    {
      title: t('approvals.applicant'),
      key: 'applicant',
      render: (_, record) => userNames.get(record.applicantId) ?? record.applicantId,
    },
    {
      title: t('common.status'),
      dataIndex: 'taskStatus',
      key: 'taskStatus',
      render: (s: TaskInstanceStatus) => <Tag color={statusColors[s]}>{s}</Tag>,
    },
    {
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
    { title: t('approvals.updated'), dataIndex: 'updatedAt', key: 'updatedAt', render: (v: string) => new Date(v).toLocaleString() },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => (
        <Button size="small" onClick={() => navigate(`/tasks/${record.taskId}`)}>
          {t('tasks.openTask')}
        </Button>
      ),
    },
  ];

  const emptyDescriptions: Record<StatusTab, string> = {
    Pending: t('approvals.emptyPending'),
    Returned: t('approvals.emptyReturned'),
    Completed: t('approvals.emptyCompleted'),
    All: t('approvals.emptyAll'),
  };

  return (
    <div>
      <Space style={{ marginBottom: 16, width: '100%', justifyContent: 'space-between', flexWrap: 'wrap' }}>
        <Space wrap>
          <Segmented<StatusTab>
            value={status}
            onChange={(value) => updateParams({ status: value, page: 1 })}
            options={['Pending', 'Returned', 'Completed', 'All']}
          />
          <Input.Search
            placeholder={t('approvals.searchPlaceholder')}
            allowClear
            defaultValue={search}
            style={{ width: 260 }}
            onSearch={(value) => updateParams({ search: value, page: 1 })}
          />
          <Button icon={<ReloadOutlined />} onClick={() => query.refetch()} loading={query.isFetching}>
            {t('common.refresh')}
          </Button>
        </Space>
      </Space>

      {query.isError ? (
        <ApiErrorAlert error={query.error} title={t('approvals.loadFailed')} />
      ) : (
        <Table<ApprovalWorklistItem>
          rowKey="taskId"
          columns={columns}
          dataSource={query.data?.items ?? []}
          loading={query.isLoading}
          locale={{ emptyText: emptyDescriptions[status] }}
          pagination={{
            current: page,
            pageSize,
            total: query.data?.totalCount ?? 0,
            showSizeChanger: true,
            onChange: (nextPage, nextPageSize) => updateParams({ page: nextPage, pageSize: nextPageSize }),
          }}
        />
      )}
    </div>
  );
}
