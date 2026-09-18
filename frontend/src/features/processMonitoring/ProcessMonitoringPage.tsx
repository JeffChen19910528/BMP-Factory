import { useMemo, useState } from 'react';
import { ReloadOutlined } from '@ant-design/icons';
import { Button, DatePicker, Input, Select, Space, Table, Tag, Typography } from 'antd';
import type { TableProps } from 'antd';
import { useNavigate } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { useUsersById } from '../../hooks/useUsers';
import type { ProcessInstanceStatus } from '../../types/process';
import type { ProcessMonitoringItem, ProcessMonitoringSlaStatus } from '../../types/processMonitoring';
import { useTranslation } from '../../i18n/LanguageContext';
import { useProcessMonitoring } from './hooks';

const { RangePicker } = DatePicker;

// Phase 7.2.1 — every persisted status this enum actually reaches; Cancelled/Suspended/Failed are
// reserved-but-unreachable (see types/process.ts's own comment) but still get a color so the UI
// never crashes if the backend enum is ever extended, rather than assuming only three values.
const statusColors: Record<ProcessInstanceStatus, string> = {
  Running: 'blue',
  Completed: 'green',
  Rejected: 'red',
  Cancelled: 'default',
  Suspended: 'orange',
  Failed: 'red',
};

const slaStatusColors: Record<ProcessMonitoringSlaStatus, string> = {
  Active: 'blue',
  Warning: 'gold',
  Overdue: 'red',
  Completed: 'green',
};

// Phase 7.2.1 — Process Monitoring Query + List. Replaces the previous ComingSoon placeholder at
// this same /instances route (no second "Process Monitoring" route/nav item was created). Every
// number/status here comes directly from GET /api/process-monitoring — this page never computes
// Overdue, current-task resolution, or SLA aggregation itself (Part 21/29/31: backend truth only).
// Process Detail / Timeline are explicitly out of scope (Phase 7.2.2) — "View Task" links to the
// existing, already-built Task Detail page for the process's current task; there is no process
// instance detail route to link to yet, so no action is offered for a process with no current task
// (e.g. Completed/Rejected) rather than faking one.
export function ProcessMonitoringPage() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const { users } = useUsersById();

  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<ProcessInstanceStatus | undefined>(undefined);
  const [slaStatus, setSlaStatus] = useState<ProcessMonitoringSlaStatus | undefined>(undefined);
  const [initiatorId, setInitiatorId] = useState<string | undefined>(undefined);
  const [dateRange, setDateRange] = useState<[string, string] | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);

  const query = useMemo(
    () => ({
      search: search || undefined,
      status,
      slaStatus,
      initiatorId,
      startedFrom: dateRange?.[0],
      startedTo: dateRange?.[1],
      page,
      pageSize,
    }),
    [search, status, slaStatus, initiatorId, dateRange, page, pageSize],
  );

  const monitoringQuery = useProcessMonitoring(query);

  function resetFilters() {
    setSearch('');
    setStatus(undefined);
    setSlaStatus(undefined);
    setInitiatorId(undefined);
    setDateRange(null);
    setPage(1);
  }

  const columns: TableProps<ProcessMonitoringItem>['columns'] = [
    {
      title: 'Process',
      key: 'process',
      render: (_, record) => (
        <div>
          <div>{record.processDefinitionName}</div>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.processDefinitionKey}
          </Typography.Text>
        </div>
      ),
    },
    {
      title: 'Status',
      dataIndex: 'status',
      key: 'status',
      render: (s: ProcessInstanceStatus) => <Tag color={statusColors[s] ?? 'default'}>{s}</Tag>,
    },
    {
      title: 'Initiator',
      key: 'initiator',
      render: (_, record) => record.initiatorDisplayName,
    },
    {
      title: 'Current Task',
      key: 'currentTask',
      render: (_, record) =>
        record.currentTaskName ? (
          <div>
            <div>{record.currentTaskName}</div>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {record.currentTaskAssigneeDisplay ?? '—'}
            </Typography.Text>
          </div>
        ) : (
          <Typography.Text type="secondary">—</Typography.Text>
        ),
    },
    {
      title: 'SLA',
      key: 'sla',
      render: (_, record) =>
        record.slaStatus ? <Tag color={slaStatusColors[record.slaStatus]}>{record.slaStatus}</Tag> : <Typography.Text type="secondary">—</Typography.Text>,
    },
    { title: 'Started', dataIndex: 'startedAt', key: 'startedAt', render: (v: string) => new Date(v).toLocaleString() },
    { title: 'Updated', dataIndex: 'updatedAt', key: 'updatedAt', render: (v: string) => new Date(v).toLocaleString() },
    {
      title: 'Actions',
      key: 'actions',
      render: (_, record) => (
        <Space>
          <Button size="small" onClick={() => navigate(`/instances/${record.processInstanceId}`)}>
            Details
          </Button>
          {record.currentTaskId && (
            <Button size="small" onClick={() => navigate(`/tasks/${record.currentTaskId}`)}>
              View Task
            </Button>
          )}
        </Space>
      ),
    },
  ];

  return (
    <div>
      <Typography.Title level={3}>{t("monitoring.title")}</Typography.Title>

      <Space style={{ marginBottom: 16 }} wrap>
        <Input
          placeholder="Search process or initiator"
          allowClear
          style={{ width: 240 }}
          value={search}
          onChange={(e) => {
            setSearch(e.target.value);
            setPage(1);
          }}
        />
        <Select<ProcessInstanceStatus | undefined>
          placeholder="Status"
          allowClear
          style={{ width: 140 }}
          value={status}
          onChange={(v) => {
            setStatus(v);
            setPage(1);
          }}
          options={[
            { value: 'Running', label: 'Running' },
            { value: 'Completed', label: 'Completed' },
            { value: 'Rejected', label: 'Rejected' },
          ]}
        />
        <Select<ProcessMonitoringSlaStatus | undefined>
          placeholder="SLA"
          allowClear
          style={{ width: 140 }}
          value={slaStatus}
          onChange={(v) => {
            setSlaStatus(v);
            setPage(1);
          }}
          options={[
            { value: 'Active', label: 'Active' },
            { value: 'Warning', label: 'Warning' },
            { value: 'Overdue', label: 'Overdue' },
            { value: 'Completed', label: 'Completed' },
          ]}
        />
        <Select
          placeholder="Initiator"
          allowClear
          showSearch
          optionFilterProp="label"
          style={{ width: 200 }}
          value={initiatorId}
          onChange={(v) => {
            setInitiatorId(v);
            setPage(1);
          }}
          options={users.map((u) => ({ value: u.id, label: u.displayName }))}
        />
        <RangePicker
          showTime
          onChange={(values) => {
            if (!values || !values[0] || !values[1]) {
              setDateRange(null);
            } else {
              setDateRange([values[0].toISOString(), values[1].toISOString()]);
            }
            setPage(1);
          }}
        />
        <Button onClick={resetFilters}>Reset</Button>
        <Button icon={<ReloadOutlined />} onClick={() => monitoringQuery.refetch()} loading={monitoringQuery.isFetching}>
          Refresh
        </Button>
      </Space>

      {monitoringQuery.isError ? (
        <ApiErrorAlert error={monitoringQuery.error} title="Failed to load process monitoring data" />
      ) : (
        <Table<ProcessMonitoringItem>
          rowKey="processInstanceId"
          columns={columns}
          dataSource={monitoringQuery.data?.items ?? []}
          loading={monitoringQuery.isLoading}
          locale={{ emptyText: 'No process instances match your filters.' }}
          pagination={{
            current: page,
            pageSize,
            total: monitoringQuery.data?.totalCount ?? 0,
            showSizeChanger: true,
            showTotal: (total) => `${total} process instance${total === 1 ? '' : 's'}`,
            onChange: (nextPage, nextPageSize) => {
              setPage(nextPage);
              setPageSize(nextPageSize);
            },
          }}
        />
      )}
    </div>
  );
}
