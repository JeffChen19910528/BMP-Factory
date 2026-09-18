import { Handle, Position, type NodeProps } from '@xyflow/react';
import { Tag, Typography } from 'antd';
import { WarningOutlined } from '@ant-design/icons';
import type { WorkflowFlowNode } from './graphModel';
import { useTranslation } from '../../../i18n/LanguageContext';

const nodeColors: Record<string, { border: string; background: string }> = {
  Start: { border: '#52c41a', background: '#f6ffed' },
  End: { border: '#ff4d4f', background: '#fff1f0' },
  UserTask: { border: '#1677ff', background: '#f0f7ff' },
  ApprovalTask: { border: '#722ed1', background: '#f9f0ff' },
};

// Single render component for every node type — it reads WorkflowNodeData.nodeType to decide
// its shape/handles rather than registering one React Flow node type per workflow node type,
// which keeps ReactFlow's nodeTypes map (and this file) from growing with every future node kind.
export function WorkflowNode({ data, selected }: NodeProps<WorkflowFlowNode>) {
  const { t } = useTranslation();
  const colors = nodeColors[data.nodeType] ?? { border: '#d9d9d9', background: '#fafafa' };
  const showSourceHandle = data.nodeType !== 'End';
  const showTargetHandle = data.nodeType !== 'Start';

  return (
    <div
      style={{
        border: `2px solid ${selected ? '#faad14' : colors.border}`,
        background: data.supported ? colors.background : '#f5f5f5',
        borderRadius: data.nodeType === 'Start' || data.nodeType === 'End' ? 24 : 8,
        padding: '10px 16px',
        minWidth: 140,
        textAlign: 'center',
        boxShadow: selected ? '0 0 0 2px rgba(250,173,20,0.3)' : '0 1px 3px rgba(0,0,0,0.1)',
      }}
      title={data.supported ? undefined : t('processDesigner.unsupportedNodeTooltip')}
    >
      {showTargetHandle && <Handle type="target" position={Position.Left} />}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6 }}>
        {!data.supported && <WarningOutlined style={{ color: '#faad14' }} />}
        <Typography.Text strong style={{ fontSize: 13 }}>
          {data.name}
        </Typography.Text>
      </div>
      <div style={{ marginTop: 4 }}>
        <Tag style={{ marginInlineEnd: 0 }} color={data.supported ? undefined : 'warning'}>
          {data.nodeType}
        </Tag>
      </div>
      {showSourceHandle && <Handle type="source" position={Position.Right} />}
    </div>
  );
}

export const workflowNodeTypes = { workflowNode: WorkflowNode };
