import { Alert, Typography } from 'antd';
import { CheckCircleOutlined, WarningOutlined } from '@ant-design/icons';
import type { WorkflowValidationResult } from '../../../types/process';
import type { WorkflowFlowNode } from './graphModel';
import { useTranslation } from '../../../i18n/LanguageContext';

export interface ResolvedValidationError {
  code: string;
  message: string;
  node: { id: string; name: string; nodeType: string } | null;
}

// Backend WorkflowValidationError only carries {code, message} — no structured nodeId field — so
// mapping an error back to a node is a best-effort heuristic: every single-quoted token in the
// message is checked against the graph's current node ids, and the first match wins. Documented
// as a heuristic, not a contract (Phase 5.3.2 §7: "do not fabricate node mappings" — an error that
// matches nothing resolves to `node: null` and is shown in the global list, never guessed at).
export function resolveValidationErrors(
  result: WorkflowValidationResult | null,
  nodes: WorkflowFlowNode[],
): ResolvedValidationError[] {
  if (!result) return [];
  const byId = new Map(nodes.map((n) => [n.id, n]));

  return result.errors.map((error) => {
    for (const match of error.message.matchAll(/'([^']+)'/g)) {
      const node = byId.get(match[1]);
      if (node) {
        return { code: error.code, message: error.message, node: { id: node.id, name: node.data.name, nodeType: node.data.nodeType } };
      }
    }
    return { code: error.code, message: error.message, node: null };
  });
}

interface ValidationPanelProps {
  result: WorkflowValidationResult | null;
  nodes: WorkflowFlowNode[];
  onSelectNode: (nodeId: string) => void;
}

// Dedicated validation result area (Phase 5.3.2 §8) — backend validation is authoritative
// throughout; this only renders whatever WorkflowDefinitionValidator (via POST
// /api/process-definitions/validate) already decided, it never re-judges validity itself.
export function ValidationPanel({ result, nodes, onSelectNode }: ValidationPanelProps) {
  const { t } = useTranslation();
  if (!result) return null;

  if (result.isValid) {
    return (
      <Alert
        style={{ margin: '8px 16px 0' }}
        type="success"
        showIcon
        icon={<CheckCircleOutlined />}
        message={t('processDesigner.workflowValid')}
      />
    );
  }

  const resolved = resolveValidationErrors(result, nodes);

  return (
    <div style={{ margin: '8px 16px 0', border: '1px solid #ffccc7', borderRadius: 6, background: '#fff2f0' }}>
      <div style={{ padding: '8px 12px', borderBottom: '1px solid #ffccc7', fontWeight: 600 }}>
        {t('processDesigner.validationHeader')}
      </div>
      <ul style={{ margin: 0, padding: '4px 0', listStyle: 'none' }}>
        {resolved.map((error, i) => (
          <li
            key={i}
            onClick={error.node ? () => onSelectNode(error.node!.id) : undefined}
            style={{
              padding: '6px 12px',
              cursor: error.node ? 'pointer' : 'default',
              borderBottom: i < resolved.length - 1 ? '1px solid #ffe7e4' : undefined,
            }}
          >
            <WarningOutlined style={{ color: '#cf1322', marginInlineEnd: 6 }} />
            {error.node ? (
              <Typography.Text>
                <Typography.Text strong>
                  {error.node.nodeType} "{error.node.name}"
                </Typography.Text>
                {' — '}
                {error.message}
              </Typography.Text>
            ) : (
              <Typography.Text>{error.message}</Typography.Text>
            )}
            <Typography.Text type="secondary" style={{ marginInlineStart: 8, fontSize: 12 }}>
              {error.code}
            </Typography.Text>
          </li>
        ))}
      </ul>
    </div>
  );
}
