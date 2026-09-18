import { useMemo, useState } from 'react';
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { Button, Input, Space, Table, Typography } from 'antd';
import type { TableProps } from 'antd';
import { QueryStateView } from '../../components/QueryStateView';
import type { Department } from '../../types/department';
import { useTranslation } from '../../i18n/LanguageContext';
import { useDepartments, useOrganizations, useUsers } from './hooks';
import { CreateDepartmentModal } from './CreateDepartmentModal';
import { EditDepartmentModal } from './EditDepartmentModal';

// Departments workspace (Phase 5.5.2 §8-10). Hierarchy is displayed via a "Parent" column
// resolving parentId -> name (the backend already models Department.ParentId; no separate
// org-hierarchy model is introduced here) rather than a tree widget — the existing Department
// list has no depth/ordering concept to build a real tree view from, and inventing one would be
// exactly the "duplicate business rules in React" the spec warns against.
export function DepartmentsPage() {
  const { t } = useTranslation();
  const departmentsQuery = useDepartments();
  const organizationsQuery = useOrganizations();
  const usersQuery = useUsers();
  const departments = departmentsQuery.data ?? [];
  const organizations = organizationsQuery.data ?? [];
  const users = usersQuery.data ?? [];

  const [search, setSearch] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [editingDepartment, setEditingDepartment] = useState<Department | null>(null);

  const departmentNameById = useMemo(() => new Map(departments.map((d) => [d.id, d.name])), [departments]);
  const userNameById = useMemo(() => new Map(users.map((u) => [u.id, u.displayName])), [users]);

  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase();
    if (!term) return departments;
    return departments.filter((d) => d.name.toLowerCase().includes(term));
  }, [departments, search]);

  async function handleReload(departmentId: string): Promise<Department | undefined> {
    const result = await departmentsQuery.refetch();
    return result.data?.find((d) => d.id === departmentId);
  }

  const columns: TableProps<Department>['columns'] = [
    { title: t('common.name'), dataIndex: 'name', key: 'name' },
    {
      title: t('administration.parent'),
      dataIndex: 'parentId',
      key: 'parentId',
      render: (id: string | null) => (id ? departmentNameById.get(id) ?? '—' : <Typography.Text type="secondary">{t('administration.topLevel')}</Typography.Text>),
    },
    {
      title: t('administration.manager'),
      dataIndex: 'managerUserId',
      key: 'managerUserId',
      render: (id: string | null) => (id ? userNameById.get(id) ?? '—' : '—'),
    },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => (
        <Button size="small" onClick={() => setEditingDepartment(record)}>
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
          <Button icon={<ReloadOutlined />} onClick={() => departmentsQuery.refetch()} loading={departmentsQuery.isFetching}>
            {t('common.refresh')}
          </Button>
        </Space>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
          {t('administration.createDepartment')}
        </Button>
      </Space>

      <QueryStateView
        isLoading={departmentsQuery.isLoading}
        error={departmentsQuery.error}
        isEmpty={filtered.length === 0 && !departmentsQuery.isLoading}
        emptyDescription={t('administration.departmentsEmpty')}
      >
        <Table<Department>
          rowKey="id"
          columns={columns}
          dataSource={filtered}
          pagination={{ showSizeChanger: true, defaultPageSize: 20 }}
        />
      </QueryStateView>

      <CreateDepartmentModal
        open={createOpen}
        departments={departments}
        organizations={organizations}
        users={users}
        onClose={() => setCreateOpen(false)}
      />

      {editingDepartment && (
        <EditDepartmentModal
          open={!!editingDepartment}
          department={editingDepartment}
          departments={departments}
          users={users}
          onClose={() => setEditingDepartment(null)}
          onReload={handleReload}
        />
      )}
    </div>
  );
}
