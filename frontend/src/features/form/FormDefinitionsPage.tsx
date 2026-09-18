import { useState } from 'react';
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { Button, Space, Table, Tag, Typography } from 'antd';
import type { TableProps } from 'antd';
import { useNavigate } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { FormDefinition, FormDefinitionStatus } from '../../types/form';
import { useTranslation } from '../../i18n/LanguageContext';
import { useFormDefinitions } from './hooks';
import { CreateFormModal } from './CreateFormModal';

const statusColors: Record<FormDefinitionStatus, string> = {
  Draft: 'default',
  Published: 'green',
  Suspended: 'orange',
  Archived: 'red',
};

// Mirrors ProcessDefinitionsPage's structure, but the backend's GET /api/form-definitions has no
// search/status-filter/pagination query params (unlike GET /api/process-definitions — Phase 5.2
// added those there; Forms didn't need them for this iteration's scope), so this list is a plain
// client-rendered table over the full result rather than a server-paged one.
export function FormDefinitionsPage() {
  const navigate = useNavigate();
  const [createOpen, setCreateOpen] = useState(false);
  const query = useFormDefinitions();
  const { t } = useTranslation();

  const columns: TableProps<FormDefinition>['columns'] = [
    { title: t('common.name'), dataIndex: 'name', key: 'name' },
    { title: t('forms.key'), dataIndex: 'key', key: 'key', render: (key: string) => <Typography.Text code>{key}</Typography.Text> },
    { title: t('forms.category'), dataIndex: 'category', key: 'category', render: (c: string | null) => c ?? '—' },
    {
      title: t('common.status'),
      dataIndex: 'status',
      key: 'status',
      render: (s: FormDefinitionStatus) => <Tag color={statusColors[s]}>{s}</Tag>,
    },
    {
      title: t('processes.currentVersion'),
      dataIndex: 'currentVersionId',
      key: 'currentVersionId',
      render: (id: string | null) => (id ? t('forms.published') : '—'),
    },
    {
      title: t('common.createdAt'),
      dataIndex: 'createdAt',
      key: 'createdAt',
      render: (v: string) => new Date(v).toLocaleString(),
    },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => (
        <Button size="small" onClick={() => navigate(`/forms/${record.id}`)}>
          {t('common.view')}
        </Button>
      ),
    },
  ];

  return (
    <div>
      <Space style={{ marginBottom: 16, width: '100%', justifyContent: 'space-between', flexWrap: 'wrap' }}>
        <Button icon={<ReloadOutlined />} onClick={() => query.refetch()} loading={query.isFetching}>
          {t('common.refresh')}
        </Button>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
          {t('forms.createForm')}
        </Button>
      </Space>

      {query.isError ? (
        <ApiErrorAlert error={query.error} title={t('forms.loadFailed')} />
      ) : (
        <Table<FormDefinition>
          rowKey="id"
          columns={columns}
          dataSource={query.data ?? []}
          loading={query.isLoading}
          locale={{ emptyText: t('forms.noFormDefinitions') }}
        />
      )}

      <CreateFormModal
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        onCreated={(id) => {
          setCreateOpen(false);
          navigate(`/forms/${id}`);
        }}
      />
    </div>
  );
}
