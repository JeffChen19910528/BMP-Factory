import { useState } from 'react';
import { Alert, Button, Card, Descriptions, Space, Table, Tag, Typography } from 'antd';
import type { TableProps } from 'antd';
import { useNavigate, useParams } from 'react-router-dom';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import { QueryStateView } from '../../components/QueryStateView';
import { useUsersById } from '../../hooks/useUsers';
import type { FormVersion, FormVersionStatus } from '../../types/form';
import { useTranslation } from '../../i18n/LanguageContext';
import { CreateFormVersionModal } from './CreateFormVersionModal';
import { EditFormModal } from './EditFormModal';
import { useFormDefinition, usePublishFormVersion, useFormVersions } from './hooks';

const versionStatusColors: Record<FormVersionStatus, string> = {
  Draft: 'default',
  Published: 'green',
};

// Mirrors ProcessDetailPage's structure exactly (list -> detail -> version history -> designer).
export function FormDetailPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [editOpen, setEditOpen] = useState(false);
  const [createVersionOpen, setCreateVersionOpen] = useState(false);

  const definitionQuery = useFormDefinition(id);
  const versionsQuery = useFormVersions(id);
  const publishMutation = usePublishFormVersion(id ?? '');
  const { byId: userNames } = useUsersById();
  const { t } = useTranslation();

  const versions = versionsQuery.data ?? [];
  const hasDraftVersion = versions.some((v) => v.status === 'Draft');

  const columns: TableProps<FormVersion>['columns'] = [
    { title: t('forms.version'), dataIndex: 'versionNumber', key: 'versionNumber', render: (v: number) => `v${v}` },
    {
      title: t('common.status'),
      dataIndex: 'status',
      key: 'status',
      render: (s: FormVersionStatus) => <Tag color={versionStatusColors[s]}>{s}</Tag>,
    },
    { title: t('common.createdAt'), dataIndex: 'createdAt', key: 'createdAt', render: (v: string) => new Date(v).toLocaleString() },
    { title: t('forms.createdBy'), dataIndex: 'createdBy', key: 'createdBy', render: (v: string | null) => (v ? userNames.get(v) ?? v : '—') },
    {
      title: t('forms.publishedColumn'),
      dataIndex: 'publishedAt',
      key: 'publishedAt',
      render: (v: string | null) => (v ? new Date(v).toLocaleString() : '—'),
    },
    { title: t('forms.publishedByColumn'), dataIndex: 'publishedBy', key: 'publishedBy', render: (v: string | null) => (v ? userNames.get(v) ?? v : '—') },
    {
      title: t('forms.fields'),
      key: 'fields',
      render: (_, record) => record.schema.fields.length,
    },
    {
      title: t('common.actions'),
      key: 'actions',
      render: (_, record) => (
        <Button size="small" onClick={() => navigate(`/forms/${id}/versions/${record.id}`)}>
          {record.status === 'Draft' ? t('common.edit') : t('common.view')}
        </Button>
      ),
    },
  ];

  return (
    <QueryStateView isLoading={definitionQuery.isLoading} error={definitionQuery.error}>
      {definitionQuery.data && (
        <div>
          <Card
            title={definitionQuery.data.name}
            extra={
              <Space>
                <Button onClick={() => setEditOpen(true)}>{t('common.edit')}</Button>
                <Button onClick={() => setCreateVersionOpen(true)} disabled={hasDraftVersion}>
                  {t('forms.createVersion')}
                </Button>
                <Button
                  type="primary"
                  disabled={!hasDraftVersion}
                  loading={publishMutation.isPending}
                  onClick={() => publishMutation.mutate()}
                >
                  {t('common.publish')}
                </Button>
              </Space>
            }
          >
            {hasDraftVersion && (
              <Alert
                type="info"
                showIcon
                style={{ marginBottom: 16 }}
                message={t('forms.unpublishedDraftNotice')}
              />
            )}
            {publishMutation.isError && <ApiErrorAlert error={publishMutation.error} title={t('forms.publishFailed')} />}
            <Descriptions column={2} bordered size="small">
              <Descriptions.Item label={t('forms.key')}>
                <Typography.Text code>{definitionQuery.data.key}</Typography.Text>
              </Descriptions.Item>
              <Descriptions.Item label={t('common.status')}>
                <Tag>{definitionQuery.data.status}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label={t('forms.category')}>{definitionQuery.data.category ?? '—'}</Descriptions.Item>
              <Descriptions.Item label={t('common.createdAt')}>{new Date(definitionQuery.data.createdAt).toLocaleString()}</Descriptions.Item>
              <Descriptions.Item label={t('common.description')} span={2}>
                {definitionQuery.data.description ?? '—'}
              </Descriptions.Item>
            </Descriptions>
          </Card>

          <Card title={t('processes.versionHistory')} style={{ marginTop: 16 }}>
            <QueryStateView
              isLoading={versionsQuery.isLoading}
              error={versionsQuery.error}
              isEmpty={versions.length === 0}
              emptyDescription={t('forms.noVersionsYet')}
            >
              <Table<FormVersion> rowKey="id" columns={columns} dataSource={versions} pagination={false} />
            </QueryStateView>
          </Card>

          <EditFormModal open={editOpen} definition={definitionQuery.data} onClose={() => setEditOpen(false)} />
          <CreateFormVersionModal
            open={createVersionOpen}
            formDefinitionId={definitionQuery.data.id}
            onClose={() => setCreateVersionOpen(false)}
          />
        </div>
      )}
    </QueryStateView>
  );
}
