import { useMemo, useState } from 'react';
import { ReloadOutlined } from '@ant-design/icons';
import { Button, Card, Col, DatePicker, Row, Select, Space, Statistic, Table, Tooltip, Typography } from 'antd';
import dayjs from 'dayjs';
import { useNavigate } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { useUsersById } from '../../hooks/useUsers';
import { listDepartments } from '../../services/departmentService';
import { useQuery } from '@tanstack/react-query';
import type { AnalyticsGranularity, NodeAnalyticsItem, ProcessComparisonItem, SlaTrendPoint, VolumeTrendPoint } from '../../types/analytics';
import type { ProcessInstanceStatus } from '../../types/process';
import { useTranslation } from '../../i18n/LanguageContext';
import { useAnalyticsOverview } from './hooks';

const { RangePicker } = DatePicker;

function formatHours(h: number | null): string {
  if (h === null) return '—';
  if (h < 48) return `${h.toFixed(1)}h`;
  return `${(h / 24).toFixed(1)}d`;
}

function formatRate(r: number | null): string {
  return r === null ? '—' : `${(r * 100).toFixed(1)}%`;
}

// Phase 7.4 — Analytics. Historical trends/durations/bottleneck-descriptive metrics over the same
// authorized scope Dashboard/Process Monitoring/Reporting already compute — "how is behavior
// changing / where is time being spent" (Part 4), never "what's happening now" (that's Process
// Monitoring/Dashboard) or "what happened" (that's Reporting). Every number here comes straight
// from GET /api/analytics/overview — this page never computes a trend/duration/compliance figure
// itself. Deliberately no chart library (Part 38 — no chart library existed in this project
// before this page, and every metric here is expressible as an AntD Statistic/Table without one) —
// tables/cards only, matching Reporting's own "tables and cards are sufficient" precedent.
// No predictive/AI features anywhere on this page (explicitly out of scope, Part 26/64).
export function AnalyticsPage() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const { users } = useUsersById();
  const { data: departments } = useQuery({ queryKey: ['departments'], queryFn: listDepartments, staleTime: 5 * 60 * 1000 });

  // Part 8 — bounded default range (last 30 days), matching Reporting's own established default.
  const [dateRange, setDateRange] = useState<[string, string] | null>(() => {
    const to = new Date();
    const from = new Date(to.getTime() - 30 * 24 * 60 * 60 * 1000);
    return [from.toISOString(), to.toISOString()];
  });
  const [processDefinitionId, setProcessDefinitionId] = useState<string | undefined>(undefined);
  const [status, setStatus] = useState<ProcessInstanceStatus | undefined>(undefined);
  const [initiatorId, setInitiatorId] = useState<string | undefined>(undefined);
  const [departmentId, setDepartmentId] = useState<string | undefined>(undefined);
  const [granularity, setGranularity] = useState<AnalyticsGranularity>('Day');

  const filters = useMemo(
    () => ({
      from: dateRange?.[0],
      to: dateRange?.[1],
      processDefinitionId,
      status,
      initiatorId,
      departmentId,
      granularity,
    }),
    [dateRange, processDefinitionId, status, initiatorId, departmentId, granularity],
  );

  const overviewQuery = useAnalyticsOverview(filters);

  function resetFilters() {
    const to = new Date();
    const from = new Date(to.getTime() - 30 * 24 * 60 * 60 * 1000);
    setDateRange([from.toISOString(), to.toISOString()]);
    setProcessDefinitionId(undefined);
    setStatus(undefined);
    setInitiatorId(undefined);
    setDepartmentId(undefined);
    setGranularity('Day');
  }

  const processDefinitionOptions = (overviewQuery.data?.processComparison ?? []).map((c) => ({
    value: c.processDefinitionId,
    label: c.processDefinitionName,
  }));

  const volumeColumns = [
    { title: 'Period', dataIndex: 'bucketStart', key: 'bucketStart', render: (v: string) => new Date(v).toLocaleDateString() },
    { title: 'Started', dataIndex: 'started', key: 'started' },
    { title: 'Completed', dataIndex: 'completed', key: 'completed' },
    { title: 'Rejected', dataIndex: 'rejected', key: 'rejected' },
  ];

  const slaColumns = [
    { title: 'Period', dataIndex: 'bucketStart', key: 'bucketStart', render: (v: string) => new Date(v).toLocaleDateString() },
    { title: 'Completed SLA Tasks', dataIndex: 'completedSlaTasks', key: 'completedSlaTasks' },
    { title: 'Compliant', dataIndex: 'compliantCount', key: 'compliantCount' },
    { title: 'Breached', dataIndex: 'breachedCount', key: 'breachedCount' },
    { title: 'Compliance Rate', key: 'rate', render: (_: unknown, r: SlaTrendPoint) => formatRate(r.complianceRate) },
  ];

  const nodeColumns = [
    { title: 'Node', dataIndex: 'nodeName', key: 'nodeName' },
    { title: 'Executions', dataIndex: 'executions', key: 'executions' },
    { title: 'Completed', dataIndex: 'completed', key: 'completed' },
    {
      title: 'Avg Duration (Completed)',
      key: 'avgDuration',
      render: (_: unknown, r: NodeAnalyticsItem) => (
        <Tooltip title={`Sample size: ${r.duration.sampleCount}`}>{formatHours(r.duration.averageHours)}</Tooltip>
      ),
    },
    { title: 'Overdue (ever)', dataIndex: 'overdueCount', key: 'overdueCount' },
  ];

  const comparisonColumns = [
    {
      title: 'Process',
      key: 'process',
      render: (_: unknown, r: ProcessComparisonItem) => (
        <div>
          <div>{r.processDefinitionName}</div>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {r.processDefinitionKey}
          </Typography.Text>
        </div>
      ),
    },
    { title: 'Total', dataIndex: 'total', key: 'total' },
    { title: 'Completed', dataIndex: 'completed', key: 'completed' },
    { title: 'Rejected', dataIndex: 'rejected', key: 'rejected' },
    {
      title: 'Avg Duration (Completed Instances)',
      key: 'avgDuration',
      render: (_: unknown, r: ProcessComparisonItem) => (
        <Tooltip title={`Sample size: ${r.duration.sampleCount}`}>{formatHours(r.duration.averageHours)}</Tooltip>
      ),
    },
    { title: 'SLA Compliance', key: 'sla', render: (_: unknown, r: ProcessComparisonItem) => formatRate(r.slaComplianceRate) },
    { title: 'Overdue (ever)', dataIndex: 'overdueCount', key: 'overdueCount' },
    {
      title: 'Actions',
      key: 'actions',
      // Part 45/46 — navigates to the existing Process Monitoring list (same authorization,
      // no second detail page). Process Monitoring's own filter UI has no ProcessDefinitionId
      // selector today, so the process name/key is shown for the user to search by, rather than
      // this phase adding a new filter control to that already-hardened page.
      render: () => (
        <Button size="small" onClick={() => navigate('/instances')}>
          View in Process Monitoring
        </Button>
      ),
    },
  ];

  const overview = overviewQuery.data;

  return (
    <div>
      <Typography.Title level={3}>{t("analytics.title")}</Typography.Title>
      <Typography.Paragraph type="secondary">
        Historical trends and duration analytics — descriptive only, not predictive. For "what happened during this period," see Reporting; for "what's happening now," see Process Monitoring or the Dashboard.
      </Typography.Paragraph>

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
          }}
        />
        <Select
          placeholder="Process"
          allowClear
          showSearch
          optionFilterProp="label"
          style={{ width: 200 }}
          value={processDefinitionId}
          onChange={setProcessDefinitionId}
          options={processDefinitionOptions}
        />
        <Select<ProcessInstanceStatus | undefined>
          placeholder="Status"
          allowClear
          style={{ width: 140 }}
          value={status}
          onChange={setStatus}
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
          onChange={setInitiatorId}
          options={users.map((u) => ({ value: u.id, label: u.displayName }))}
        />
        <Select
          placeholder="Department"
          allowClear
          showSearch
          optionFilterProp="label"
          style={{ width: 200 }}
          value={departmentId}
          onChange={setDepartmentId}
          options={(departments ?? []).map((d) => ({ value: d.id, label: d.name }))}
        />
        <Select<AnalyticsGranularity>
          style={{ width: 120 }}
          value={granularity}
          onChange={setGranularity}
          options={[
            { value: 'Day', label: 'Daily' },
            { value: 'Week', label: 'Weekly' },
            { value: 'Month', label: 'Monthly' },
          ]}
        />
        <Button onClick={resetFilters}>Reset</Button>
        <Button icon={<ReloadOutlined />} onClick={() => overviewQuery.refetch()} loading={overviewQuery.isFetching}>
          Refresh
        </Button>
      </Space>

      {overviewQuery.isError ? (
        <ApiErrorAlert error={overviewQuery.error} title="Failed to load analytics" />
      ) : (
        <>
          <Row gutter={16} style={{ marginBottom: 16 }}>
            <Col span={6}>
              <Card loading={overviewQuery.isLoading}>
                <Statistic title="Total Processes" value={overview?.totalProcesses ?? 0} />
                <Typography.Text type="secondary">
                  Running {overview?.runningProcesses ?? 0} · Completed {overview?.completedProcesses ?? 0} · Rejected {overview?.rejectedProcesses ?? 0}
                </Typography.Text>
              </Card>
            </Col>
            <Col span={6}>
              <Card loading={overviewQuery.isLoading}>
                <Statistic title="Avg Process Duration (Completed Instances)" value={formatHours(overview?.processDuration.averageHours ?? null)} />
                <Typography.Text type="secondary">Sample size: {overview?.processDuration.sampleCount ?? 0}</Typography.Text>
              </Card>
            </Col>
            <Col span={6}>
              <Card loading={overviewQuery.isLoading}>
                <Statistic title="Avg Task Duration (Completed Tasks)" value={formatHours(overview?.taskDuration.averageHours ?? null)} />
                <Typography.Text type="secondary">Sample size: {overview?.taskDuration.sampleCount ?? 0}</Typography.Text>
              </Card>
            </Col>
            <Col span={6}>
              <Card loading={overviewQuery.isLoading}>
                <Statistic
                  title="SLA Compliance (Completed SLA Tasks)"
                  value={formatRate(
                    overview && overview.slaTrend.length > 0
                      ? overview.slaTrend.reduce((sum, p) => sum + p.compliantCount, 0) / Math.max(1, overview.slaTrend.reduce((sum, p) => sum + p.completedSlaTasks, 0))
                      : null,
                  )}
                />
                <Typography.Text type="secondary">
                  Completed SLA tasks: {overview?.slaTrend.reduce((sum, p) => sum + p.completedSlaTasks, 0) ?? 0}
                </Typography.Text>
              </Card>
            </Col>
          </Row>

          <Card title={t("analytics.volumeTrend")} style={{ marginBottom: 16 }} loading={overviewQuery.isLoading}>
            <Table<VolumeTrendPoint>
              size="small"
              rowKey="bucketStart"
              pagination={false}
              dataSource={overview?.volumeTrend ?? []}
              locale={{ emptyText: 'No process activity in the selected range.' }}
              columns={volumeColumns}
            />
          </Card>

          <Card title={t("analytics.slaCompliance")} style={{ marginBottom: 16 }} loading={overviewQuery.isLoading}>
            <Table<SlaTrendPoint>
              size="small"
              rowKey="bucketStart"
              pagination={false}
              dataSource={overview?.slaTrend ?? []}
              locale={{ emptyText: 'No completed SLA tasks in the selected range.' }}
              columns={slaColumns}
            />
          </Card>

          <Card title={t("analytics.processComparison")} style={{ marginBottom: 16 }} loading={overviewQuery.isLoading}>
            <Table<ProcessComparisonItem>
              size="small"
              rowKey="processDefinitionId"
              pagination={false}
              dataSource={overview?.processComparison ?? []}
              locale={{ emptyText: 'No process instances match your filters.' }}
              columns={comparisonColumns}
            />
          </Card>

          <Card title={t("analytics.nodeAnalytics")} loading={overviewQuery.isLoading}>
            <Table<NodeAnalyticsItem>
              size="small"
              rowKey={(r) => `${r.nodeId}`}
              pagination={false}
              dataSource={overview?.nodeAnalytics ?? []}
              locale={{ emptyText: 'No task activity in the selected range.' }}
              columns={nodeColumns}
            />
          </Card>
        </>
      )}
    </div>
  );
}
