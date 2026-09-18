import { useMemo, useState } from 'react';
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { Button, Input, Space, Table, Tooltip } from 'antd';
import type { TableProps } from 'antd';
import { QueryStateView } from '../../components/QueryStateView';
import type { Role } from '../../types/role';
import { useTranslation } from '../../i18n/LanguageContext';
import { useRoles, useUsers } from './hooks';
import { CreateRoleModal } from './CreateRoleModal';
import { RoleMembersModal } from './RoleMembersModal';
import { EditRoleModal } from './EditRoleModal';

const ADMINISTRATOR_ROLE_NAME = 'Administrator';

// Roles workspace (Phase 5.5.2 §11-13). No new RBAC engine — Role/UserRole already existed; this
// phase only added visibility (MemberCount, GetMembersAsync) and Unassign to what already existed.
export function RolesPage() {
  const { t } = useTranslation();
  const rolesQuery = useRoles();
  const usersQuery = useUsers();
  const roles = rolesQuery.data ?? [];
  const users = usersQuery.data ?? [];

  const [search, setSearch] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [membersRole, setMembersRole] = useState<Role | null>(null);
  const [editingRole, setEditingRole] = useState<Role | null>(null);

  async function handleReload(roleId: string): Promise<Role | undefined> {
    const result = await rolesQuery.refetch();
    return result.data?.find((r) => r.id === roleId);
  }

  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase();
    if (!term) return roles;
    return roles.filter((r) => r.name.toLowerCase().includes(term));
  }, [roles, search]);

  const columns: TableProps<Role>['columns'] = [
    { title: t('common.name'), dataIndex: 'name', key: 'name' },
    { title: t('administration.members'), dataIndex: 'memberCount', key: 'memberCount' },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => {
        const isAdministrator = record.name === ADMINISTRATOR_ROLE_NAME;
        return (
          <Space>
            <Button size="small" onClick={() => setMembersRole(record)}>
              {t('administration.manageMembers')}
            </Button>
            <Tooltip title={isAdministrator ? t('administration.administratorCannotBeRenamed') : undefined}>
              <Button size="small" disabled={isAdministrator} onClick={() => setEditingRole(record)}>
                {t('administration.rename')}
              </Button>
            </Tooltip>
          </Space>
        );
      },
    },
  ];

  return (
    <div>
      <Space style={{ margin: '16px 0', width: '100%', justifyContent: 'space-between', flexWrap: 'wrap' }}>
        <Space wrap>
          <Input.Search placeholder={t('administration.searchByName')} allowClear style={{ width: 260 }} onSearch={setSearch} />
          <Button icon={<ReloadOutlined />} onClick={() => rolesQuery.refetch()} loading={rolesQuery.isFetching}>
            {t('common.refresh')}
          </Button>
        </Space>
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
          {t('administration.createRole')}
        </Button>
      </Space>

      <QueryStateView
        isLoading={rolesQuery.isLoading}
        error={rolesQuery.error}
        isEmpty={filtered.length === 0 && !rolesQuery.isLoading}
        emptyDescription={t('administration.rolesEmpty')}
      >
        <Table<Role>
          rowKey="id"
          columns={columns}
          dataSource={filtered}
          pagination={{ showSizeChanger: true, defaultPageSize: 20 }}
        />
      </QueryStateView>

      <CreateRoleModal open={createOpen} onClose={() => setCreateOpen(false)} />

      {membersRole && (
        <RoleMembersModal open={!!membersRole} role={membersRole} users={users} onClose={() => setMembersRole(null)} />
      )}

      {editingRole && (
        <EditRoleModal open={!!editingRole} role={editingRole} onClose={() => setEditingRole(null)} onReload={handleReload} />
      )}
    </div>
  );
}
