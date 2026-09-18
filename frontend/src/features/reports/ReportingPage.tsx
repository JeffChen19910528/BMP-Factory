import { useMemo, useState } from 'react';
import { DownloadOutlined, ReloadOutlined } from '@ant-design/icons';
import { Button, Card, Col, DatePicker, Row, Select, Space, Statistic, Table, Tag, Typography, message } from 'antd';
import type { TableProps } from 'antd';
import dayjs from 'dayjs';
import { useNavigate } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { useUsersById } from '../../hooks/useUsers';
import { listDepartments } from '../../services/departmentService';
import { useQuery } from '@tanstack/react-query';
import { exportReportCsv } from '../../services/reportService';
import { toApiError } from '../../services/apiClient';
import type { ProcessInstanceStatus } from '../../types/process';
import type { ProcessMonitoringSlaStatus } from '../../types/processMonitoring';
import type { ReportDetailItem } from '../../types/report';
import { useTranslation } from '../../i18n/LanguageContext';
import { useReportDetails, useReportSummary } from './hooks';

const { RangePicker } = DatePicker;

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

// Phase 7.3 — Reporting. Historical/aggregate view over the same authorized ProcessInstance scope
// Dashboard/Process Monitoring already compute (Part 14 — this answers "what happened during this
// period," not "what's happening now"). Every number here comes straight from
// GET /api/reports/summary/details/export — this page never computes Process/Task/Approval/SLA
// aggregates itself, and never recomputes SLA state from wall-clock time. This is Reporting, not
// Analytics (Part 27/51) — tables and summary cards only, no chart library, no trend/forecast/
// heat-map/report-builder UI.
//
// Visible to any authenticated user (not Administrator-only, Part 26) — [Authorize] on the
// backend controller scopes data by caller identity exactly like Dashboard/Process Monitoring
// already do; this page adds no frontend-only authorization decision of its own.
//
// Filter state is local component state, not URL query params — matching the established
// Process Monitoring / Audit Logs convention in this codebase (Part 29), not a new URL-state
// framework.
export function ReportingPage() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const { users } = useUsersById();
  const { data: departments } = useQuery({ queryKey: ['departments'], queryFn: listDepartments, staleTime: 5 * 60 * 1000 });

  // Part 16 — bounded default range (last 30 days) rather than defaulting to the entire database.
  const [dateRange, setDateRange] = useState<[string, string] | null>(() => {
    const to = new Date();
    const from = new Date(to.getTime() - 30 * 24 * 60 * 60 * 1000);
    return [from.toISOString(), to.toISOString()];
  });
  const [processDefinitionId, setProcessDefinitionId] = useState<string | undefined>(undefined);
  const [status, setStatus] = useState<ProcessInstanceStatus | undefined>(undefined);
  const [initiatorId, setInitiatorId] = useState<string | undefined>(undefined);
  const [departmentId, setDepartmentId] = useState<string | undefined>(undefined);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [exporting, setExporting] = useState(false);

  const filters = useMemo(
    () => ({
      from: dateRange?.[0],
      to: dateRange?.[1],
      processDefinitionId,
      status,
      initiatorId,
      departmentId,
    }),
    [dateRange, processDefinitionId, status, initiatorId, departmentId],
  );

  const summaryQuery = useReportSummary(filters);
  const detailQuery = useReportDetails({ ...filters, page, pageSize });

  // The process definitions offered in the filter are exactly the ones the current breakdown
  // shows — never a second, independently-fetched "all process definitions" list that could
  // silently include ones the caller isn't authorized to see any instance of.
  const processDefinitionOptions = (summaryQuery.data?.processBreakdown ?? []).map((b) => ({
    value: b.processDefinitionId,
    label: b.processDefinitionName,
  }));

  function resetFilters() {
    const to = new Date();
    const from = new Date(to.getTime() - 30 * 24 * 60 * 60 * 1000);
    setDateRange([from.toISOString(), to.toISOString()]);
    setProcessDefinitionId(undefined);
    setStatus(undefined);
    setInitiatorId(undefined);
    setDepartmentId(undefined);
    setPage(1);
  }

  async function handleExport() {
    setExporting(true);
    try {
      const blob = await exportReportCsv(filters);
      const url = URL.createObjectURL(blob);
      try {
        const link = document.createElement('a');
        link.href = url;
        link.download = `report-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '')}.csv`;
        document.body.appendChild(link);
        link.click();
        link.remove();
      } finally {
        URL.revokeObjectURL(url);
      }
    } catch (error) {
      message.error(toApiError(error).message);
    } finally {
      setExporting(false);
    }
  }

  const columns: TableProps<ReportDetailItem>['columns'] = [
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
    { title: 'Initiator', key: 'initiator', render: (_, record) => record.initiatorDisplayName },
    { title: 'Started', dataIndex: 'startedAt', key: 'startedAt', render: (v: string) => new Date(v).toLocaleString() },
    {
      title: 'Current Task',
      key: 'currentTask',
      render: (_, record) => record.currentTaskName ?? <Typography.Text type="secondary">—</Typography.Text>,
    },
    {
      title: 'SLA',
      key: 'sla',
      render: (_, record) =>
        record.slaStatus ? <Tag color={slaStatusColors[record.slaStatus]}>{record.slaStatus}</Tag> : <Typography.Text type="secondary">—</Typography.Text>,
    },
    { title: 'SLA Due', dataIndex: 'slaDueAt', key: 'slaDueAt', render: (v: string | null) => (v ? new Date(v).toLocaleString() : '—') },
    {
      title: 'Actions',
      key: 'actions',
      render: (_, record) => (
        <Button size="small" onClick={() => navigate(`/instances/${record.processInstanceId}`)}>
          Details
        </Button>
      ),
    },
  ];

  const process = summaryQuery.data?.process;
  const task = summaryQuery.data?.taskSummary;
  const approval = summaryQuery.data?.approvalSummary;
  const sla = summaryQuery.data?.slaSummary;

  return (
    <div>
      <Typography.Title level={3}>{t("reports.title")}</Typography.Title>

      <Space style={{ marginBottom: 16 }} wrap>
        <RangePicker
          showTime
          value={dateRange ? [dayjs(dateRange[0]), dayjs(dateRange[1])] : null}
          onChange={(values) => {
            if (!values || !values[0] || !values[1]) {
              setDateRange(null);
            } else {
              setDateRange([values[0].toISOString(), values[1].toISOString()]);
            }
            setPage(1);
          }}
        />
        <Select
          placeholder="Process"
          allowClear
          showSearch
          optionFilterProp="label"
          style={{ width: 200 }}
          value={processDefinitionId}
          onChange={(v) => {
            setProcessDefinitionId(v);
            setPage(1);
          }}
          options={processDefinitionOptions}
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
        <Select
          placeholder="Department"
          allowClear
          showSearch
          optionFilterProp="label"
          style={{ width: 200 }}
          value={departmentId}
          onChange={(v) => {
            setDepartmentId(v);
            setPage(1);
          }}
          options={(departments ?? []).map((d) => ({ value: d.id, label: d.name }))}
        />
        <Button onClick={resetFilters}>Reset</Button>
        <Button icon={<ReloadOutlined />} onClick={() => { summaryQuery.refetch(); detailQuery.refetch(); }} loading={summaryQuery.isFetching || detailQuery.isFetching}>
          Refresh
        </Button>
        <Button icon={<DownloadOutlined />} onClick={handleExport} loading={exporting}>
          Export CSV
        </Button>
      </Space>

      {summaryQuery.isError ? (
        <ApiErrorAlert error={summaryQuery.error} title="Failed to load report summary" />
      ) : (
        <>
          <Row gutter={16} style={{ marginBottom: 16 }}>
            <Col span={6}>
              <Card loading={summaryQuery.isLoading}>
                <Statistic title="Total Processes" value={process?.total ?? 0} />
                <Typography.Text type="secondary">
                  Running {process?.running ?? 0} · Completed {process?.completed ?? 0} · Rejected {process?.rejected ?? 0}
                </Typography.Text>
              </Card>
            </Col>
            <Col span={6}>
              <Card loading={summaryQuery.isLoading}>
                <Statistic title="Total Tasks" value={task?.total ?? 0} />
                <Typography.Text type="secondary">
                  Pending {task?.pending ?? 0} · In Progress {task?.inProgress ?? 0} · Completed {task?.completed ?? 0}
                </Typography.Text>
              </Card>
            </Col>
            <Col span={6}>
              <Card loading={summaryQuery.isLoading}>
                <Statistic title="Total Approvals" value={approval?.total ?? 0} />
                <Typography.Text type="secondary">
                  Pending {approval?.pending ?? 0} · Approved {approval?.approved ?? 0} · Rejected {approval?.rejected ?? 0}
                </Typography.Text>
              </Card>
            </Col>
            <Col span={6}>
              <Card loading={summaryQuery.isLoading}>
                <Statistic
                  title="SLA Compliance"
                  value={sla?.complianceRate != null ? `${(sla.complianceRate * 100).toFixed(1)}%` : '—'}
                />
                <Typography.Text type="secondary">
                  Active {sla?.active ?? 0} · Warning {sla?.warning ?? 0} · Overdue {sla?.overdue ?? 0} · Completed {sla?.completed ?? 0}
                </Typography.Text>
              </Card>
            </Col>
          </Row>

          <Card title={t("reports.breakdown")} style={{ marginBottom: 16 }} loading={summaryQuery.isLoading}>
            <Table
              size="small"
              rowKey="processDefinitionId"
              pagination={false}
              dataSource={summaryQuery.data?.processBreakdown ?? []}
              locale={{ emptyText: 'No process instances match your filters.' }}
              columns={[
                { title: 'Process', key: 'name', render: (_, r) => r.processDefinitionName },
                { title: 'Key', dataIndex: 'processDefinitionKey', key: 'key' },
                { title: 'Total', dataIndex: 'total', key: 'total' },
                { title: 'Running', dataIndex: 'running', key: 'running' },
                { title: 'Completed', dataIndex: 'completed', key: 'completed' },
                { title: 'Rejected', dataIndex: 'rejected', key: 'rejected' },
              ]}
            />
          </Card>
        </>
      )}

      <Card title={t("reports.detail")}>
        {detailQuery.isError ? (
          <ApiErrorAlert error={detailQuery.error} title="Failed to load report detail" />
        ) : (
          <Table<ReportDetailItem>
            rowKey="processInstanceId"
            columns={columns}
            dataSource={detailQuery.data?.items ?? []}
            loading={detailQuery.isLoading}
            locale={{ emptyText: 'No process instances match your filters.' }}
            pagination={{
              current: page,
              pageSize,
              total: detailQuery.data?.totalCount ?? 0,
              showSizeChanger: true,
              showTotal: (total) => `${total} process instance${total === 1 ? '' : 's'}`,
              onChange: (nextPage, nextPageSize) => {
                setPage(nextPage);
                setPageSize(nextPageSize);
              },
            }}
          />
        )}
      </Card>
    </div>
  );
}

