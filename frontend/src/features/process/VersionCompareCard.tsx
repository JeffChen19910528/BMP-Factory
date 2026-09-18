import { useState } from 'react';
import { Button, Card, Select, Space, Table, Tag, Typography } from 'antd';
import { ApiErrorAlert } from '../../components/ApiErrorAlert';
import type { ModifiedNode, ModifiedTransition, NodeSummary, ProcessVersion, TransitionSummary } from '../../types/process';
import { useTranslation } from '../../i18n/LanguageContext';
import { useCompareProcessVersions } from './hooks';

// Phase 8 — Version Comparison UI. Structural diff only, backend-computed — this component never
// recomputes or reinterprets the comparison; it just renders VersionComparisonResponse. Every
// list is a small table (Part 39: "Do not show raw JSON diff as the primary UI").
export function VersionCompareCard({ processDefinitionId, versions }: { processDefinitionId: string; versions: ProcessVersion[] }) {
  const { t } = useTranslation();
  const [fromVersionId, setFromVersionId] = useState<string | undefined>(undefined);
  const [toVersionId, setToVersionId] = useState<string | undefined>(undefined);
  const [active, setActive] = useState<{ from?: string; to?: string }>({});

  const compareQuery = useCompareProcessVersions(processDefinitionId, active.from, active.to);

  const options = versions
    .slice()
    .sort((a, b) => a.versionNumber - b.versionNumber)
    .map((v) => ({ value: v.id, label: `v${v.versionNumber} (${v.status})` }));

  const diff = compareQuery.data;

  return (
    <Card title={t('processes.versionComparisonTitle')} style={{ marginTop: 16 }}>
      <Space style={{ marginBottom: 16 }} wrap>
        <Select placeholder={t('processes.fromVersion')} style={{ width: 200 }} options={options} value={fromVersionId} onChange={setFromVersionId} />
        <Select placeholder={t('processes.toVersion')} style={{ width: 200 }} options={options} value={toVersionId} onChange={setToVersionId} />
        <Button
          type="primary"
          disabled={!fromVersionId || !toVersionId}
          loading={compareQuery.isFetching}
          onClick={() => setActive({ from: fromVersionId, to: toVersionId })}
        >
          {t('processes.compare')}
        </Button>
      </Space>

      {compareQuery.isError && <ApiErrorAlert error={compareQuery.error} title={t('processes.comparisonFailed')} />}

      {diff && (
        <div>
          <Typography.Paragraph strong>{diff.summary}</Typography.Paragraph>

          {diff.addedNodes.length > 0 && (
            <NodeTable title={t('processes.addedNodes')} color="green" rows={diff.addedNodes} nodeLabel={t('processes.node')} typeLabel={t('processes.type')} />
          )}
          {diff.removedNodes.length > 0 && (
            <NodeTable title={t('processes.removedNodes')} color="red" rows={diff.removedNodes} nodeLabel={t('processes.node')} typeLabel={t('processes.type')} />
          )}
          {diff.modifiedNodes.length > 0 && (
            <div style={{ marginBottom: 16 }}>
              <Typography.Text strong>{t('processes.modifiedNodes')}</Typography.Text>
              <Table<ModifiedNode>
                size="small"
                pagination={false}
                rowKey="nodeId"
                dataSource={diff.modifiedNodes}
                columns={[
                  { title: t('processes.node'), key: 'name', render: (_, r) => (r.fromName === r.toName ? r.fromName : `${r.fromName} → ${r.toName}`) },
                  {
                    title: t('processes.changedFields'),
                    key: 'changedFields',
                    render: (_, r) => r.changedFields.map((f) => <Tag key={f}>{f}</Tag>),
                  },
                ]}
              />
            </div>
          )}

          {diff.addedTransitions.length > 0 && (
            <TransitionTable title={t('processes.addedTransitions')} color="green" rows={diff.addedTransitions} transitionLabel={t('processes.transition')} sourceLabel={t('processes.source')} targetLabel={t('processes.target')} />
          )}
          {diff.removedTransitions.length > 0 && (
            <TransitionTable title={t('processes.removedTransitions')} color="red" rows={diff.removedTransitions} transitionLabel={t('processes.transition')} sourceLabel={t('processes.source')} targetLabel={t('processes.target')} />
          )}
          {diff.modifiedTransitions.length > 0 && (
            <div style={{ marginBottom: 16 }}>
              <Typography.Text strong>{t('processes.modifiedTransitions')}</Typography.Text>
              <Table<ModifiedTransition>
                size="small"
                pagination={false}
                rowKey="transitionId"
                dataSource={diff.modifiedTransitions}
                columns={[
                  { title: t('processes.transition'), dataIndex: 'transitionId' },
                  { title: t('processes.changedFields'), key: 'changedFields', render: (_, r) => r.changedFields.map((f) => <Tag key={f}>{f}</Tag>) },
                ]}
              />
            </div>
          )}
        </div>
      )}
    </Card>
  );
}

function NodeTable({ title, color, rows, nodeLabel, typeLabel }: { title: string; color: string; rows: NodeSummary[]; nodeLabel: string; typeLabel: string }) {
  return (
    <div style={{ marginBottom: 16 }}>
      <Typography.Text strong>{title}</Typography.Text>
      <Table<NodeSummary>
        size="small"
        pagination={false}
        rowKey="nodeId"
        dataSource={rows}
        columns={[
          { title: nodeLabel, dataIndex: 'name' },
          { title: typeLabel, dataIndex: 'type', render: (nodeType: string) => <Tag color={color}>{nodeType}</Tag> },
        ]}
      />
    </div>
  );
}

function TransitionTable({
  title,
  color,
  rows,
  transitionLabel,
  sourceLabel,
  targetLabel,
}: {
  title: string;
  color: string;
  rows: TransitionSummary[];
  transitionLabel: string;
  sourceLabel: string;
  targetLabel: string;
}) {
  return (
    <div style={{ marginBottom: 16 }}>
      <Typography.Text strong>{title}</Typography.Text>
      <Table<TransitionSummary>
        size="small"
        pagination={false}
        rowKey="transitionId"
        dataSource={rows}
        columns={[
          { title: transitionLabel, dataIndex: 'transitionId', render: (id: string) => <Tag color={color}>{id}</Tag> },
          { title: sourceLabel, dataIndex: 'source' },
          { title: targetLabel, dataIndex: 'target' },
        ]}
      />
    </div>
  );
}
