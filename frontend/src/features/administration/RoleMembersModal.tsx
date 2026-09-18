import { Checkbox, Input, List, Modal, Tooltip, Typography, message } from 'antd';
import { useMemo, useState } from 'react';
import { toApiError } from '../../services/apiClient';
import { useAuthStore } from '../../stores/authStore';
import type { Role } from '../../types/role';
import type { User } from '../../types/user';
import { useTranslation } from '../../i18n/LanguageContext';
import { useAssignRole, useRoleMembers, useUnassignRole } from './hooks';

interface RoleMembersModalProps {
  open: boolean;
  role: Role;
  users: User[];
  onClose: () => void;
}

const ADMINISTRATOR_ROLE_NAME = 'Administrator';

// Roles workspace member management (Phase 5.5.2 §12): a checkbox-style member list backed
// directly by GET .../members + POST assign/unassign — every toggle round-trips through the
// backend, which is the actual authorization boundary (a user can never grant themselves
// Administrator here regardless of what this UI allows, since RolesController is
// [Authorize(Roles="Administrator")] end to end).
export function RoleMembersModal({ open, role, users, onClose }: RoleMembersModalProps) {
  const { t } = useTranslation();
  const currentUserId = useAuthStore((state) => state.user?.userId);
  const membersQuery = useRoleMembers(open ? role.id : undefined);
  const assignMutation = useAssignRole(role.id);
  const unassignMutation = useUnassignRole(role.id);
  const [search, setSearch] = useState('');

  const memberIds = useMemo(() => new Set((membersQuery.data ?? []).map((u) => u.id)), [membersQuery.data]);

  const filteredUsers = useMemo(() => {
    const term = search.trim().toLowerCase();
    if (!term) return users;
    return users.filter((u) => u.displayName.toLowerCase().includes(term) || u.username.toLowerCase().includes(term));
  }, [users, search]);

  function toggle(user: User, isMember: boolean) {
    if (isMember) {
      // Self-lockout protection (§26): an administrator removing their OWN Administrator
      // membership is rejected server-side (CANNOT_REMOVE_OWN_ADMINISTRATOR_ROLE) — the checkbox
      // is already disabled for that exact case below, so reaching here means it's safe to ask
      // for confirmation like any other membership removal (§25).
      Modal.confirm({
        title: t('administration.removeMemberConfirmTitle'),
        content: `${t('administration.confirmRemoveMemberPrefix')}${user.displayName}${t('administration.confirmRemoveMemberMiddle')}${role.name}${t('administration.confirmRemoveMemberSuffix')}`,
        okText: t('administration.remove'),
        okButtonProps: { danger: true },
        onOk: () =>
          unassignMutation.mutate(
            { userId: user.id, roleId: role.id },
            {
              onError: (error) => message.error(toApiError(error).message),
            },
          ),
      });
    } else {
      assignMutation.mutate(
        { userId: user.id, roleId: role.id },
        {
          onError: (error) => message.error(toApiError(error).message),
        },
      );
    }
  }

  return (
    <Modal title={`${t('administration.membersOf')} "${role.name}"`} open={open} onCancel={onClose} footer={null} destroyOnHidden>
      <Input.Search placeholder={t('administration.searchUsers')} allowClear style={{ marginBottom: 12 }} onSearch={setSearch} onChange={(e) => setSearch(e.target.value)} />
      <List
        loading={membersQuery.isLoading}
        dataSource={filteredUsers}
        style={{ maxHeight: 400, overflowY: 'auto' }}
        renderItem={(user) => {
          const isMember = memberIds.has(user.id);
          const isSelfAdministrator = user.id === currentUserId && role.name === ADMINISTRATOR_ROLE_NAME && isMember;
          const checkbox = (
            <Checkbox
              checked={isMember}
              disabled={isSelfAdministrator || assignMutation.isPending || unassignMutation.isPending}
              onChange={() => toggle(user, isMember)}
            >
              {user.displayName} <Typography.Text type="secondary">({user.username})</Typography.Text>
            </Checkbox>
          );
          return (
            <List.Item>
              {isSelfAdministrator ? (
                <Tooltip title={t('errors.CANNOT_REMOVE_OWN_ADMINISTRATOR_ROLE')}>{checkbox}</Tooltip>
              ) : (
                checkbox
              )}
            </List.Item>
          );
        }}
      />
    </Modal>
  );
}
