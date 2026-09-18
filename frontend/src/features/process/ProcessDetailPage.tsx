import { useState } from 'react';
import { Alert, Button, Card, Descriptions, Input, Modal, Popconfirm, Select, Space, Table, Tag, Typography, message } from 'antd';
import type { TableProps } from 'antd';
import { useNavigate, useParams } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { QueryStateView } from '../../components/QueryStateView';
import { useUsersById } from '../../hooks/useUsers';
import { useAuthStore } from '../../stores/authStore';
import { toApiError } from '../../services/apiClient';
import type { ProcessDefinitionStatus, ProcessVersion, ProcessVersionStatus } from '../../types/process';
import { useTranslation } from '../../i18n/LanguageContext';
import { CreateVersionModal } from './CreateVersionModal';
import { EditProcessModal } from './EditProcessModal';
import { VersionCompareCard } from './VersionCompareCard';
import {
  useArchiveProcessDefinition,
  useAssignProcessOwner,
  useProcessDefinition,
  usePublishProcessVersion,
  useProcessVersions,
  useRestoreProcessDefinition,
  useSuspendProcessDefinition,
} from './hooks';

const versionStatusColors: Record<ProcessVersionStatus, string> = {
  Draft: 'default',
  Published: 'green',
};

const definitionStatusColors: Record<ProcessDefinitionStatus, string> = {
  Draft: 'default',
  Published: 'green',
  Suspended: 'orange',
  Archived: 'red',
};

// Phase 8 — Process Governance & Lifecycle. Every action here is UX only — the backend is always
// authoritative (Suspend/Archive/Restore re-check Administrator-or-Owner server-side; a hidden or
// disabled button here is never the real security boundary, see CLAUDE.md's AdminRoute note for
// the same established principle applied elsewhere in this app).
export function ProcessDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { t } = useTranslation();
  const [editOpen, setEditOpen] = useState(false);
  const [createVersionOpen, setCreateVersionOpen] = useState(false);
  const [publishOpen, setPublishOpen] = useState(false);
  const [changeReason, setChangeReason] = useState('');
  const [ownerModalOpen, setOwnerModalOpen] = useState(false);
  const [ownerSelection, setOwnerSelection] = useState<string | undefined>(undefined);

  const currentUser = useAuthStore((s) => s.user);
  const isAdministrator = currentUser?.roles.includes('Administrator') ?? false;

  const definitionQuery = useProcessDefinition(id);
  const versionsQuery = useProcessVersions(id);
  const publishMutation = usePublishProcessVersion(id ?? '');
  const suspendMutation = useSuspendProcessDefinition(id ?? '');
  const archiveMutation = useArchiveProcessDefinition(id ?? '');
  const restoreMutation = useRestoreProcessDefinition(id ?? '');
  const assignOwnerMutation = useAssignProcessOwner(id ?? '');
  const { byId: userNames, users } = useUsersById();

  const versions = versionsQuery.data ?? [];
  const hasDraftVersion = versions.some((v) => v.status === 'Draft');
  const definition = definitionQuery.data;

  // Any owner-or-administrator-only action being unavailable when the caller isn't either is
  // enforced by the backend regardless of what's shown here — this only hides an action that
  // would otherwise just come back with a 403.
  const isOwnerOrAdmin = isAdministrator || (!!currentUser && definition?.ownerUserId === currentUser.userId);

  function handleGovernanceError(error: unknown) {
    message.error(toApiError(error).message);
  }

  function openPublishModal() {
    setChangeReason('');
    setPublishOpen(true);
  }

  function handlePublish() {
    publishMutation.mutate(
      { changeReason: changeReason.trim() || null },
      {
        onSuccess: () => setPublishOpen(false),
        onError: handleGovernanceError,
      },
    );
  }

  function openOwnerModal() {
    setOwnerSelection(definition?.ownerUserId ?? undefined);
    setOwnerModalOpen(true);
  }

  function handleAssignOwner() {
    if (!definition) return;
    assignOwnerMutation.mutate(
      { ownerUserId: ownerSelection ?? null, expectedVersion: definition.rowVersion },
      {
        onSuccess: () => setOwnerModalOpen(false),
        onError: handleGovernanceError,
      },
    );
  }

  function handleSuspend() {
    if (!definition) return;
    suspendMutation.mutate({ expectedVersion: definition.rowVersion }, { onError: handleGovernanceError });
  }

  function handleArchive() {
    if (!definition) return;
    archiveMutation.mutate({ expectedVersion: definition.rowVersion }, { onError: handleGovernanceError });
  }

  function handleRestore() {
    if (!definition) return;
    restoreMutation.mutate({ expectedVersion: definition.rowVersion }, { onError: handleGovernanceError });
  }

  const columns: TableProps<ProcessVersion>['columns'] = [
    { title: t('processes.version'), dataIndex: 'versionNumber', key: 'versionNumber', render: (v: number) => `v${v}` },
    {
      title: t('common.status'),
      dataIndex: 'status',
      key: 'status',
      render: (s: ProcessVersionStatus) => <Tag color={versionStatusColors[s]}>{s}</Tag>,
    },
    { title: t('common.createdAt'), dataIndex: 'createdAt', key: 'createdAt', render: (v: string) => new Date(v).toLocaleString() },
    { title: t('processes.createdBy'), dataIndex: 'createdBy', key: 'createdBy', render: (v: string | null) => (v ? userNames.get(v) ?? v : '—') },
    {
      title: t('processes.publishedAt'),
      dataIndex: 'publishedAt',
      key: 'publishedAt',
      render: (v: string | null) => (v ? new Date(v).toLocaleString() : '—'),
    },
    { title: t('processes.publishedBy'), dataIndex: 'publishedBy', key: 'publishedBy', render: (v: string | null) => (v ? userNames.get(v) ?? v : '—') },
    {
      title: t('processes.changeReason'),
      dataIndex: 'changeReason',
      key: 'changeReason',
      render: (v: string | null) => v ?? '—',
    },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => (
        <Button size="small" onClick={() => navigate(`/processes/${id}/versions/${record.id}`)}>
          {record.status === 'Draft' ? t('common.edit') : t('common.view')}
        </Button>
      ),
    },
  ];

  return (
    <QueryStateView isLoading={definitionQuery.isLoading} error={definitionQuery.error}>
      {definition && (
        <div>
          <Card
            title={definition.name}
            extra={
              <Space wrap>
                <Button onClick={() => setEditOpen(true)}>{t('common.edit')}</Button>
                <Button onClick={() => setCreateVersionOpen(true)} disabled={hasDraftVersion}>
                  {t('processes.createVersion')}
                </Button>
                <Button type="primary" disabled={!hasDraftVersion} loading={publishMutation.isPending} onClick={openPublishModal}>
                  {t('common.publish')}
                </Button>
                {definition.status === 'Published' && isOwnerOrAdmin && (
                  <Popconfirm
                    title={t('governance.suspendConfirm')}
                    description={t('processes.suspendDescription')}
                    onConfirm={handleSuspend}
                    okText={t('common.suspend')}
                  >
                    <Button loading={suspendMutation.isPending}>{t('common.suspend')}</Button>
                  </Popconfirm>
                )}
                {(definition.status === 'Published' || definition.status === 'Suspended') && isOwnerOrAdmin && (
                  <Popconfirm
                    title={t('governance.archiveConfirm')}
                    description={t('processes.archiveDescription')}
                    onConfirm={handleArchive}
                    okText={t('common.archive')}
                  >
                    <Button danger loading={archiveMutation.isPending}>
                      {t('common.archive')}
                    </Button>
                  </Popconfirm>
                )}
                {(definition.status === 'Suspended' || definition.status === 'Archived') && isOwnerOrAdmin && (
                  <Popconfirm
                    title={t('governance.restoreConfirm')}
                    description={t('processes.restoreDescription')}
                    onConfirm={handleRestore}
                    okText={t('common.restore')}
                  >
                    <Button type="primary" loading={restoreMutation.isPending}>
                      {t('common.restore')}
                    </Button>
                  </Popconfirm>
                )}
              </Space>
            }
          >
            {hasDraftVersion && (
              <Alert
                type="info"
                showIcon
                style={{ marginBottom: 16 }}
                message={t('processes.draftVersionAlert')}
              />
            )}
            {(definition.status === 'Suspended' || definition.status === 'Archived') && (
              <Alert
                type="warning"
                showIcon
                style={{ marginBottom: 16 }}
                message={`${t('processes.notEditableAlertPrefix')} ${definition.status}. ${t('processes.notEditableAlertSuffix')}`}
              />
            )}
            {publishMutation.isError && <ApiErrorAlert error={publishMutation.error} title={t('processes.publishFailed')} />}
            {suspendMutation.isError && <ApiErrorAlert error={suspendMutation.error} title={t('processes.suspendFailed')} />}
            {archiveMutation.isError && <ApiErrorAlert error={archiveMutation.error} title={t('processes.archiveFailed')} />}
            {restoreMutation.isError && <ApiErrorAlert error={restoreMutation.error} title={t('processes.restoreFailed')} />}
            <Descriptions column={2} bordered size="small">
              <Descriptions.Item label={t('processes.key')}>
                <Typography.Text code>{definition.key}</Typography.Text>
              </Descriptions.Item>
              <Descriptions.Item label={t('common.status')}>
                <Tag color={definitionStatusColors[definition.status]}>{definition.status}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label={t('processes.category')}>{definition.category ?? '—'}</Descriptions.Item>
              <Descriptions.Item label={t('common.createdAt')}>{new Date(definition.createdAt).toLocaleString()}</Descriptions.Item>
              <Descriptions.Item label={t('processes.owner')}>
                <Space>
                  {definition.ownerUserId ? userNames.get(definition.ownerUserId) ?? definition.ownerUserId : t('processes.noOwner')}
                  {isAdministrator && (
                    <Button size="small" onClick={openOwnerModal}>
                      {t('processes.changeOwner')}
                    </Button>
                  )}
                </Space>
              </Descriptions.Item>
              <Descriptions.Item label={t('processes.currentVersion')}>
                {definition.currentVersionId
                  ? `v${versions.find((v) => v.id === definition.currentVersionId)?.versionNumber ?? '?'}`
                  : '—'}
              </Descriptions.Item>
              <Descriptions.Item label={t('common.description')} span={2}>
                {definition.description ?? '—'}
              </Descriptions.Item>
            </Descriptions>
          </Card>

          <Card title={t('processes.versionHistory')} style={{ marginTop: 16 }}>
            <QueryStateView
              isLoading={versionsQuery.isLoading}
              error={versionsQuery.error}
              isEmpty={versions.length === 0}
              emptyDescription={t('processes.noVersionsYet')}
            >
              <Table<ProcessVersion> rowKey="id" columns={columns} dataSource={versions} pagination={false} />
            </QueryStateView>
          </Card>

          <VersionCompareCard processDefinitionId={definition.id} versions={versions} />

          <EditProcessModal open={editOpen} definition={definition} onClose={() => setEditOpen(false)} />
          <CreateVersionModal open={createVersionOpen} processDefinitionId={definition.id} onClose={() => setCreateVersionOpen(false)} />

          <Modal
            title={t('processes.publishVersionTitle')}
            open={publishOpen}
            onCancel={() => setPublishOpen(false)}
            onOk={handlePublish}
            okText={t('common.publish')}
            confirmLoading={publishMutation.isPending}
          >
            <Typography.Paragraph type="secondary">
              {t('processes.publishReasonHint')}
            </Typography.Paragraph>
            <Input.TextArea
              rows={3}
              maxLength={1000}
              showCount
              placeholder={t('processes.changeReasonPlaceholder')}
              value={changeReason}
              onChange={(e) => setChangeReason(e.target.value)}
            />
          </Modal>

          <Modal
            title={t('processes.changeOwner')}
            open={ownerModalOpen}
            onCancel={() => setOwnerModalOpen(false)}
            onOk={handleAssignOwner}
            okText={t('common.save')}
            confirmLoading={assignOwnerMutation.isPending}
          >
            <Typography.Paragraph type="secondary">
              {t('processes.ownerDescription')}
            </Typography.Paragraph>
            <Select
              allowClear
              showSearch
              optionFilterProp="label"
              style={{ width: '100%' }}
              placeholder={t('processes.noOwner')}
              value={ownerSelection}
              onChange={setOwnerSelection}
              options={users.map((u) => ({ value: u.id, label: u.displayName }))}
            />
          </Modal>
        </div>
      )}
    </QueryStateView>
  );
}
