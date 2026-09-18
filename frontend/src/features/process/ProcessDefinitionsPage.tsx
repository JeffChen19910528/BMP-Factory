import { useState } from 'react';
import { ReloadOutlined, PlusOutlined } from '@ant-design/icons';
import { Button, Checkbox, Input, Select, Space, Table, Tag, Typography } from 'antd';
import type { TableProps } from 'antd';
import { useNavigate } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { ProcessDefinition, ProcessDefinitionStatus } from '../../types/process';
import { useTranslation } from '../../i18n/LanguageContext';
import { useProcessDefinitions } from './hooks';
import { CreateProcessModal } from './CreateProcessModal';

const statusColors: Record<ProcessDefinitionStatus, string> = {
  Draft: 'default',
  Published: 'green',
  Suspended: 'orange',
  Archived: 'red',
};

export function ProcessDefinitionsPage() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<ProcessDefinitionStatus | undefined>(undefined);
  // Phase 8 Part 33/60 — UX-only default: Archived processes are hidden from the default view
  // (nothing is hidden from the backend — the "Archived" status option and this checkbox itself
  // still let any user see them; this only affects what this page shows by default). Because the
  // backend's ProcessDefinitionQuery.Status is a single-value filter (never a list), this is
  // implemented as a client-side filter over the current page's own items rather than a second
  // "not equal to Archived" query param — a documented, deliberately small tradeoff (Part 16:
  // prefer query/application-layer only, no API shape change for a UX convenience) that can very
  // rarely under-fill a page (e.g. a page of 20 rows showing 19 after hiding one Archived row)
  // rather than something requiring a backend change.
  const [hideArchived, setHideArchived] = useState(true);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [createOpen, setCreateOpen] = useState(false);

  const query = useProcessDefinitions({ search: search || undefined, status, page, pageSize });
  const displayedItems = (query.data?.items ?? []).filter((item) => !hideArchived || status === 'Archived' || item.status !== 'Archived');

  const columns: TableProps<ProcessDefinition>['columns'] = [
    { title: t('common.name'), dataIndex: 'name', key: 'name' },
    { title: t('processes.key'), dataIndex: 'key', key: 'key', render: (key: string) => <Typography.Text code>{key}</Typography.Text> },
    { title: t('processes.category'), dataIndex: 'category', key: 'category', render: (c: string | null) => c ?? '—' },
    {
      title: t('common.status'),
      dataIndex: 'status',
      key: 'status',
      render: (s: ProcessDefinitionStatus) => <Tag color={statusColors[s]}>{s}</Tag>,
    },
    {
      title: t('processes.currentVersion'),
      dataIndex: 'currentVersionId',
      key: 'currentVersionId',
      render: (id: string | null) => (id ? 'Published' : '—'),
    },
    {
      title: t('common.createdAt'),
      dataIndex: 'createdAt',
      key: 'createdAt',
      render: (v: string) => new Date(v).toLocaleString(),
    },
    {
      title: t('common.updatedAt'),
      dataIndex: 'updatedAt',
      key: 'updatedAt',
      render: (v: string | null) => (v ? new Date(v).toLocaleString() : '—'),
    },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => (
        <Button size="small" onClick={() => navigate(`/processes/${record.id}`)}>
          {t('common.view')}
        </Button>
      ),
    },
  ];

  return (
    <div>
      <Space style={{ marginBottom: 16, width: '100%', justifyContent: 'space-between', flexWrap: 'wrap' }}>
        <Space wrap>
          <Input.Search
            placeholder={t('processes.searchPlaceholder')}
            allowClear
            style={{ width: 260 }}
            onSearch={(value) => {
              setSearch(value);
              setPage(1);
            }}
          />
          <Select<ProcessDefinitionStatus | 'All'>
            style={{ width: 160 }}
            value={status ?? 'All'}
            onChange={(value) => {
              setStatus(value === 'All' ? undefined : value);
              setPage(1);
            }}
            options={[
              { label: t('processes.allStatuses'), value: 'All' },
              { label: 'Draft', value: 'Draft' },
              { label: 'Published', value: 'Published' },
              { label: 'Suspended', value: 'Suspended' },
              { label: 'Archived', value: 'Archived' },
            ]}
          />
          <Checkbox checked={hideArchived} onChange={(e) => setHideArchived(e.target.checked)}>
            {t('processes.hideArchived')}
          </Checkbox>
          <Button icon={<ReloadOutlined />} onClick={() => query.refetch()} loading={query.isFetching}>
            {t('common.refresh')}
          </Button>
        </Space>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
          {t('processes.createProcess')}
        </Button>
      </Space>

      {query.isError ? (
        <ApiErrorAlert error={query.error} title={t('processes.loadListError')} />
      ) : (
        <Table<ProcessDefinition>
          rowKey="id"
          columns={columns}
          dataSource={displayedItems}
          loading={query.isLoading}
          locale={{ emptyText: t('processes.emptyList') }}
          pagination={{
            current: page,
            pageSize,
            total: query.data?.totalCount ?? 0,
            showSizeChanger: true,
            onChange: (nextPage, nextPageSize) => {
              setPage(nextPage);
              setPageSize(nextPageSize);
            },
          }}
        />
      )}

      <CreateProcessModal
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        onCreated={(id) => {
          setCreateOpen(false);
          navigate(`/processes/${id}`);
        }}
      />
    </div>
  );
}
