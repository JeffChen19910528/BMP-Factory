import { useMemo, useState } from 'react';
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { Button, Space, Table, Tag } from 'antd';
import type { TableProps } from 'antd';
import { QueryStateView } from '../../components/QueryStateView';
import { useProcessDefinitions } from '../process/hooks';
import type { SlaPolicy } from '../../types/slaPolicy';
import { useTranslation } from '../../i18n/LanguageContext';
import { useSlaPolicies } from './hooks';
import { CreateSlaPolicyModal } from './CreateSlaPolicyModal';
import { EditSlaPolicyModal } from './EditSlaPolicyModal';

// Phase 9 — SLA Policy Administration. The backend (SlaPolicyService) has had full CRUD since
// Phase 6.3; this is the first frontend surface for it. Mirrors DepartmentsPage.tsx's own
// structure — a flat table (no process/node tree — SlaPolicy is a small, independently-scoped
// side table, not a hierarchy) with Create/Edit modals.
export function SlaPoliciesPage() {
  const { t } = useTranslation();
  const policiesQuery = useSlaPolicies();
  const processDefinitionsQuery = useProcessDefinitions({ page: 1, pageSize: 200 });
  const policies = policiesQuery.data ?? [];
  const processNameById = useMemo(
    () => new Map((processDefinitionsQuery.data?.items ?? []).map((p) => [p.id, p.name])),
    [processDefinitionsQuery.data],
  );

  const [createOpen, setCreateOpen] = useState(false);
  const [editingPolicy, setEditingPolicy] = useState<SlaPolicy | null>(null);

  async function handleReload(policyId: string): Promise<SlaPolicy | undefined> {
    const result = await policiesQuery.refetch();
    return result.data?.find((p) => p.id === policyId);
  }

  const columns: TableProps<SlaPolicy>['columns'] = [
    {
      title: t('administration.process'),
      dataIndex: 'processDefinitionId',
      key: 'processDefinitionId',
      render: (id: string) => processNameById.get(id) ?? id,
    },
    { title: t('administration.node'), dataIndex: 'nodeId', key: 'nodeId' },
    {
      title: t('administration.enabled'),
      dataIndex: 'enabled',
      key: 'enabled',
      render: (enabled: boolean) => <Tag color={enabled ? 'green' : 'default'}>{enabled ? t('administration.enabled') : t('administration.disabled')}</Tag>,
    },
    { title: t('administration.durationMinColumn'), dataIndex: 'durationMinutes', key: 'durationMinutes' },
    { title: t('administration.warningOffsetMinColumn'), dataIndex: 'warningOffsetMinutes', key: 'warningOffsetMinutes' },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => (
        <Button size="small" onClick={() => setEditingPolicy(record)}>
          {t('common.edit')}
        </Button>
      ),
    },
  ];

  return (
    <div>
      <Space style={{ margin: '16px 0', width: '100%', justifyContent: 'space-between', flexWrap: 'wrap' }}>
        <Button icon={<ReloadOutlined />} onClick={() => policiesQuery.refetch()} loading={policiesQuery.isFetching}>
          {t('common.refresh')}
        </Button>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
          {t('administration.createSlaPolicy')}
        </Button>
      </Space>

      <QueryStateView
        isLoading={policiesQuery.isLoading}
        error={policiesQuery.error}
        isEmpty={policies.length === 0 && !policiesQuery.isLoading}
        emptyDescription={t('administration.slaPoliciesEmpty')}
      >
        <Table<SlaPolicy> rowKey="id" columns={columns} dataSource={policies} pagination={{ showSizeChanger: true, defaultPageSize: 20 }} />
      </QueryStateView>

      <CreateSlaPolicyModal open={createOpen} onClose={() => setCreateOpen(false)} />

      {editingPolicy && (
        <EditSlaPolicyModal open={!!editingPolicy} policy={editingPolicy} onClose={() => setEditingPolicy(null)} onReload={handleReload} />
      )}
    </div>
  );
}
