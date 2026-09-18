import { useMemo, useState } from 'react';
import { ReloadOutlined } from '@ant-design/icons';
import { Button, DatePicker, Input, Space, Table, Typography } from 'antd';
import type { TableProps } from 'antd';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { AuditLogEntry } from '../../types/audit';
import { useTranslation } from '../../i18n/LanguageContext';
import { useAuditLogs } from './hooks';

const { RangePicker } = DatePicker;

// Audit Logs workspace (Phase 5.5.2 §14-18): strictly read-only, no edit/delete UI or endpoint
// exists to call. Server-side search/filter/pagination via the existing AuditLogQuery params —
// the one Administration resource with real server pagination (see hooks.ts). Columns match the
// backend AuditLogDto fields exactly (Timestamp/Actor/Action/Entity/EntityId/Result) — there is
// no Result or TraceId field on AuditLogDto today, so those columns are not rendered rather than
// inventing data the backend doesn't provide.
export function AuditLogsPage() {
  const { t } = useTranslation();
  const [userId, setUserId] = useState('');
  const [entityType, setEntityType] = useState('');
  const [entityId, setEntityId] = useState('');
  const [dateRange, setDateRange] = useState<[string, string] | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);

  const query = useMemo(
    () => ({
      userId: userId || undefined,
      entityType: entityType || undefined,
      entityId: entityId || undefined,
      from: dateRange?.[0],
      to: dateRange?.[1],
      page,
      pageSize,
    }),
    [userId, entityType, entityId, dateRange, page, pageSize],
  );

  const auditQuery = useAuditLogs(query);

  const columns: TableProps<AuditLogEntry>['columns'] = [
    {
      title: t('audit.performedAt'),
      dataIndex: 'timestamp',
      key: 'timestamp',
      render: (v: string) => new Date(v).toLocaleString(),
    },
    {
      title: t('audit.performedBy'),
      dataIndex: 'userId',
      key: 'userId',
      render: (id: string | null) => (id ? <Typography.Text code>{id}</Typography.Text> : <Typography.Text type="secondary">System</Typography.Text>),
    },
    { title: t('audit.action'), dataIndex: 'action', key: 'action' },
    { title: t('audit.entityType'), dataIndex: 'entityType', key: 'entityType' },
    {
      title: t('audit.entityId'),
      dataIndex: 'entityId',
      key: 'entityId',
      render: (id: string | null) => id ?? '—',
    },
  ];

  return (
    <div>
      <Space style={{ margin: '16px 0' }} wrap>
        <Input
          placeholder={t('administration.filterByActorId')}
          allowClear
          style={{ width: 220 }}
          onChange={(e) => {
            setUserId(e.target.value);
            setPage(1);
          }}
        />
        <Input
          placeholder={t('administration.filterByEntityType')}
          allowClear
          style={{ width: 180 }}
          onChange={(e) => {
            setEntityType(e.target.value);
            setPage(1);
          }}
        />
        <Input
          placeholder={t('administration.filterByEntityId')}
          allowClear
          style={{ width: 220 }}
          onChange={(e) => {
            setEntityId(e.target.value);
            setPage(1);
          }}
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
        <Button icon={<ReloadOutlined />} onClick={() => auditQuery.refetch()} loading={auditQuery.isFetching}>
          {t('common.refresh')}
        </Button>
      </Space>

      {auditQuery.isError ? (
        <ApiErrorAlert error={auditQuery.error} title={t('administration.auditLoadError')} />
      ) : (
        <Table<AuditLogEntry>
          rowKey="id"
          columns={columns}
          dataSource={auditQuery.data?.items ?? []}
          loading={auditQuery.isLoading}
          locale={{ emptyText: t('administration.auditEmpty') }}
          pagination={{
            current: page,
            pageSize,
            total: auditQuery.data?.totalCount ?? 0,
            showSizeChanger: true,
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
