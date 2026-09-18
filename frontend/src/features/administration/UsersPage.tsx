import { useMemo, useState } from 'react';
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { Button, Input, Select, Space, Table, Tag } from 'antd';
import type { TableProps } from 'antd';
import { QueryStateView } from '../../components/QueryStateView';
import type { User } from '../../types/user';
import { useTranslation } from '../../i18n/LanguageContext';
import { useDepartments, useUsers } from './hooks';
import { CreateUserModal } from './CreateUserModal';
import { EditUserModal } from './EditUserModal';
import { ResetPasswordModal } from './ResetPasswordModal';

// Users workspace (Phase 5.5.2 §4-7): list/search/filter/pagination all done client-side over
// the full GET /api/users result, matching how the Process Designer's own user pickers already
// fetch the whole list — there is no server-side Users query/pagination endpoint to call, and
// none is added this phase (see hooks.ts's comment on this decision).
export function UsersPage() {
  const { t } = useTranslation();
  const usersQuery = useUsers();
  const departmentsQuery = useDepartments();
  const departments = departmentsQuery.data ?? [];

  const [search, setSearch] = useState('');
  const [departmentFilter, setDepartmentFilter] = useState<string | undefined>(undefined);
  const [statusFilter, setStatusFilter] = useState<'All' | 'Active' | 'Inactive'>('All');
  const [createOpen, setCreateOpen] = useState(false);
  const [editingUser, setEditingUser] = useState<User | null>(null);
  const [resettingPasswordUser, setResettingPasswordUser] = useState<User | null>(null);

  const departmentNameById = useMemo(() => new Map(departments.map((d) => [d.id, d.name])), [departments]);

  const filtered = useMemo(() => {
    const users = usersQuery.data ?? [];
    const term = search.trim().toLowerCase();
    return users.filter((u) => {
      if (term && !u.username.toLowerCase().includes(term) && !u.displayName.toLowerCase().includes(term) && !u.email.toLowerCase().includes(term)) {
        return false;
      }
      if (departmentFilter && u.departmentId !== departmentFilter) return false;
      if (statusFilter === 'Active' && !u.isActive) return false;
      if (statusFilter === 'Inactive' && u.isActive) return false;
      return true;
    });
  }, [usersQuery.data, search, departmentFilter, statusFilter]);

  async function handleReload(userId: string): Promise<User | undefined> {
    const result = await usersQuery.refetch();
    return result.data?.find((u) => u.id === userId);
  }

  const columns: TableProps<User>['columns'] = [
    { title: t('administration.username'), dataIndex: 'username', key: 'username' },
    { title: t('administration.displayName'), dataIndex: 'displayName', key: 'displayName' },
    { title: t('administration.email'), dataIndex: 'email', key: 'email' },
    {
      title: t('administration.department'),
      dataIndex: 'departmentId',
      key: 'departmentId',
      render: (id: string | null) => (id ? departmentNameById.get(id) ?? '—' : '—'),
    },
    {
      title: t('common.status'),
      dataIndex: 'isActive',
      key: 'isActive',
      render: (active: boolean) => <Tag color={active ? 'green' : 'default'}>{active ? t('administration.isActive') : t('administration.disabled')}</Tag>,
    },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => (
        <Space>
          <Button size="small" onClick={() => setEditingUser(record)}>
            {t('common.edit')}
          </Button>
          <Button size="small" onClick={() => setResettingPasswordUser(record)}>
            {t('common.resetPassword')}
          </Button>
        </Space>
      ),
    },
  ];

  return (
    <div>
      <Space style={{ margin: '16px 0', width: '100%', justifyContent: 'space-between', flexWrap: 'wrap' }}>
        <Space wrap>
          <Input.Search
            placeholder={t('administration.searchUsersFull')}
            allowClear
            style={{ width: 260 }}
            onSearch={setSearch}
          />
          <Select
            style={{ width: 180 }}
            value={departmentFilter ?? 'All'}
            onChange={(value) => setDepartmentFilter(value === 'All' ? undefined : value)}
            options={[{ label: t('administration.allDepartments'), value: 'All' }, ...departments.map((d) => ({ label: d.name, value: d.id }))]}
          />
          <Select
            style={{ width: 140 }}
            value={statusFilter}
            onChange={setStatusFilter}
            options={[
              { label: t('administration.allStatuses'), value: 'All' },
              { label: t('administration.isActive'), value: 'Active' },
              { label: t('administration.disabled'), value: 'Inactive' },
            ]}
          />
          <Button icon={<ReloadOutlined />} onClick={() => usersQuery.refetch()} loading={usersQuery.isFetching}>
            {t('common.refresh')}
          </Button>
        </Space>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
          {t('administration.createUser')}
        </Button>
      </Space>

      <QueryStateView
        isLoading={usersQuery.isLoading}
        error={usersQuery.error}
        isEmpty={filtered.length === 0 && !usersQuery.isLoading}
        emptyDescription={t('administration.usersEmpty')}
      >
        <Table<User>
          rowKey="id"
          columns={columns}
          dataSource={filtered}
          pagination={{ showSizeChanger: true, defaultPageSize: 20 }}
        />
      </QueryStateView>

      <CreateUserModal open={createOpen} departments={departments} onClose={() => setCreateOpen(false)} />

      {editingUser && (
        <EditUserModal
          open={!!editingUser}
          user={editingUser}
          departments={departments}
          onClose={() => setEditingUser(null)}
          onReload={handleReload}
        />
      )}

      {resettingPasswordUser && (
        <ResetPasswordModal
          open={!!resettingPasswordUser}
          user={resettingPasswordUser}
          onClose={() => setResettingPasswordUser(null)}
          onReload={handleReload}
        />
      )}
    </div>
  );
}
