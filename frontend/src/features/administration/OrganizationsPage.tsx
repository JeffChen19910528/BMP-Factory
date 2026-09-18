import { useMemo, useState } from 'react';
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { Button, Input, Space, Table, Typography } from 'antd';
import type { TableProps } from 'antd';
import { QueryStateView } from '../../components/QueryStateView';
import type { Organization } from '../../types/organization';
import { useTranslation } from '../../i18n/LanguageContext';
import { useOrganizations } from './hooks';
import { CreateOrganizationModal } from './CreateOrganizationModal';
import { EditOrganizationModal } from './EditOrganizationModal';

// Phase 9 — Organization Administration. Mirrors DepartmentsPage.tsx's own structure exactly
// (same "Parent" column resolving parentId -> name via a client-side Map, no tree widget — the
// existing Organization list has no depth/ordering concept to build a real tree view from).
export function OrganizationsPage() {
  const { t } = useTranslation();
  const organizationsQuery = useOrganizations();
  const organizations = organizationsQuery.data ?? [];

  const [search, setSearch] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [editingOrganization, setEditingOrganization] = useState<Organization | null>(null);

  const nameById = useMemo(() => new Map(organizations.map((o) => [o.id, o.name])), [organizations]);

  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase();
    if (!term) return organizations;
    return organizations.filter((o) => o.name.toLowerCase().includes(term));
  }, [organizations, search]);

  async function handleReload(organizationId: string): Promise<Organization | undefined> {
    const result = await organizationsQuery.refetch();
    return result.data?.find((o) => o.id === organizationId);
  }

  const columns: TableProps<Organization>['columns'] = [
    { title: t('common.name'), dataIndex: 'name', key: 'name' },
    {
      title: t('administration.parent'),
      dataIndex: 'parentId',
      key: 'parentId',
      render: (id: string | null) => (id ? nameById.get(id) ?? '—' : <Typography.Text type="secondary">{t('administration.topLevel')}</Typography.Text>),
    },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => (
        <Button size="small" onClick={() => setEditingOrganization(record)}>
          {t('common.edit')}
        </Button>
      ),
    },
  ];

  return (
    <div>
      <Space style={{ margin: '16px 0', width: '100%', justifyContent: 'space-between', flexWrap: 'wrap' }}>
        <Space wrap>
          <Input.Search placeholder={t('administration.searchByName')} allowClear style={{ width: 260 }} onSearch={setSearch} />
          <Button icon={<ReloadOutlined />} onClick={() => organizationsQuery.refetch()} loading={organizationsQuery.isFetching}>
            {t('common.refresh')}
          </Button>
        </Space>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
          {t('administration.createOrganization')}
        </Button>
      </Space>

      <QueryStateView
        isLoading={organizationsQuery.isLoading}
        error={organizationsQuery.error}
        isEmpty={filtered.length === 0 && !organizationsQuery.isLoading}
        emptyDescription={t('administration.organizationsEmpty')}
      >
        <Table<Organization> rowKey="id" columns={columns} dataSource={filtered} pagination={{ showSizeChanger: true, defaultPageSize: 20 }} />
      </QueryStateView>

      <CreateOrganizationModal open={createOpen} organizations={organizations} onClose={() => setCreateOpen(false)} />

      {editingOrganization && (
        <EditOrganizationModal
          open={!!editingOrganization}
          organization={editingOrganization}
          organizations={organizations}
          onClose={() => setEditingOrganization(null)}
          onReload={handleReload}
        />
      )}
    </div>
  );
}
