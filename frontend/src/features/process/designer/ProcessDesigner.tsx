import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Background,
  Controls,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  addEdge,
  applyEdgeChanges,
  applyNodeChanges,
  type Connection,
  type EdgeChange,
  type NodeChange,
  type ReactFlowInstance,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { Button, Layout, Space, Tag } from 'antd';
import { ExpandOutlined, RedoOutlined, UndoOutlined } from '@ant-design/icons';
import type { WorkflowDefinition, WorkflowValidationResult } from '../../../types/process';
import {
  createEdge,
  createNode,
  deserializeWorkflowDefinition,
  isConnectionAllowed,
  serializeWorkflowDefinition,
  type WorkflowFlowEdge,
  type WorkflowFlowNode,
  type WorkflowNodeData,
} from './graphModel';
import { workflowNodeTypes } from './WorkflowNode';
import { NodePalette } from './NodePalette';
import { PropertiesPanel } from './PropertiesPanel';
import { useDesignerHistory } from './useDesignerHistory';
import { ValidationPanel, resolveValidationErrors } from './ValidationPanel';
import { useTranslation } from '../../../i18n/LanguageContext';

const { Sider, Content } = Layout;

interface ProcessDesignerProps {
  initialDefinition: WorkflowDefinition;
  readOnly: boolean;
  // Fired after every committed edit (add/delete node, add/delete edge, drag-stop, a property
  // change) with the freshly re-serialized definition and whether it differs from
  // `initialDefinition` — the parent page owns the "is this different from what's on the
  // backend" comparison (it also needs to know this when switching to the JSON view) and all the
  // actual Save/Validate/Publish network calls, so this component only reports state, it never
  // calls the API itself (frontend spec §13: no HTTP logic inside a page/component beyond hooks).
  onChange: (definition: WorkflowDefinition, dirty: boolean) => void;
  onSaveDraft: () => void;
  isSaving?: boolean;
  onValidate: () => void;
  isValidating?: boolean;
  validationResult: WorkflowValidationResult | null;
  onPublish: () => void;
  isPublishing?: boolean;
  canPublish?: boolean;
}

export function ProcessDesigner({
  initialDefinition,
  readOnly,
  onChange,
  onSaveDraft,
  isSaving,
  onValidate,
  isValidating,
  validationResult,
  onPublish,
  isPublishing,
  canPublish = true,
}: ProcessDesignerProps) {
  const { t } = useTranslation();
  // Deliberately computed only once, at mount (via useState's lazy initializer, not useMemo) —
  // the parent page echoes every onChange back into its own `liveDefinition` state and passes
  // that straight back down as this same `initialDefinition` prop (so it always has the latest
  // value to hand to Save/Validate/Publish). If this baseline tracked that prop reactively, it
  // would chase the current graph on every edit and `dirty` would always read false after the
  // first change. When the parent really does want a fresh baseline (loading a different
  // version, or switching from the JSON view back to the designer), it forces a remount via the
  // `key` prop instead of relying on this prop changing.
  const [initialGraph] = useState(() => deserializeWorkflowDefinition(initialDefinition));
  const [savedJson] = useState(() => JSON.stringify(serializeWorkflowDefinition(initialGraph.nodes, initialGraph.edges)));

  const history = useDesignerHistory(initialGraph);
  const { present, commit, setLive, undo, redo, canUndo, canRedo } = history;
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null);

  const currentDefinition = useMemo(() => serializeWorkflowDefinition(present.nodes, present.edges), [present]);
  const currentJson = useMemo(() => JSON.stringify(currentDefinition), [currentDefinition]);
  const dirty = currentJson !== savedJson;

  useEffect(() => {
    onChange(currentDefinition, dirty);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentJson, dirty]);

  const resolvedValidationErrors = useMemo(
    () => resolveValidationErrors(validationResult, present.nodes),
    [validationResult, present.nodes],
  );
  const mentionedNodeIds = useMemo(
    () => new Set(resolvedValidationErrors.map((e) => e.node?.id).filter((id): id is string => !!id)),
    [resolvedValidationErrors],
  );

  const selectedNode = present.nodes.find((n) => n.id === selectedNodeId) ?? null;

  const handleNodesChange = useCallback(
    (changes: NodeChange<WorkflowFlowNode>[]) => {
      const next = applyNodeChanges(changes, present.nodes);
      const isDragging = changes.some((c) => c.type === 'position' && c.dragging);
      const structural = changes.some((c) => c.type !== 'position' && c.type !== 'select');
      if (isDragging) {
        setLive({ nodes: next, edges: present.edges });
      } else if (structural || changes.some((c) => c.type === 'position')) {
        commit({ nodes: next, edges: present.edges });
      }
    },
    [present, commit, setLive],
  );

  const handleEdgesChange = useCallback(
    (changes: EdgeChange<WorkflowFlowEdge>[]) => {
      const next = applyEdgeChanges(changes, present.edges);
      commit({ nodes: present.nodes, edges: next });
    },
    [present, commit],
  );

  const handleConnect = useCallback(
    (connection: Connection) => {
      const sourceNode = present.nodes.find((n) => n.id === connection.source);
      const targetNode = present.nodes.find((n) => n.id === connection.target);
      if (!sourceNode || !targetNode || connection.source === connection.target) return;
      if (!isConnectionAllowed(sourceNode.data.nodeType, targetNode.data.nodeType)) return;

      const edge = createEdge(connection.source, connection.target);
      commit({ nodes: present.nodes, edges: addEdge(edge, present.edges) });
    },
    [present, commit],
  );

  const isValidConnection = useCallback(
    (connection: Connection | WorkflowFlowEdge) => {
      const sourceNode = present.nodes.find((n) => n.id === connection.source);
      const targetNode = present.nodes.find((n) => n.id === connection.target);
      if (!sourceNode || !targetNode || connection.source === connection.target) return false;
      return isConnectionAllowed(sourceNode.data.nodeType, targetNode.data.nodeType);
    },
    [present.nodes],
  );

  function handleAddNode(nodeType: Parameters<typeof createNode>[0]) {
    const x = 40 + present.nodes.length * 30;
    const y = 40 + (present.nodes.length % 5) * 90;
    const node = createNode(nodeType, { x, y });
    commit({ nodes: [...present.nodes, node], edges: present.edges });
    setSelectedNodeId(node.id);
  }

  function handleDrop(event: React.DragEvent) {
    event.preventDefault();
    const nodeType = event.dataTransfer.getData('application/bpm-node-type') as Parameters<typeof createNode>[0];
    if (!nodeType) return;
    const bounds = event.currentTarget.getBoundingClientRect();
    const node = createNode(nodeType, { x: event.clientX - bounds.left, y: event.clientY - bounds.top });
    commit({ nodes: [...present.nodes, node], edges: present.edges });
    setSelectedNodeId(node.id);
  }

  function handleNodePropertyChange(nodeId: string, partial: Partial<WorkflowNodeData>) {
    const next = present.nodes.map((n) => (n.id === nodeId ? { ...n, data: { ...n.data, ...partial } } : n));
    commit({ nodes: next, edges: present.edges });
  }

  function handleDeleteNode(nodeId: string) {
    const nodes = present.nodes.filter((n) => n.id !== nodeId);
    const edges = present.edges.filter((e) => e.source !== nodeId && e.target !== nodeId);
    commit({ nodes, edges });
    if (selectedNodeId === nodeId) setSelectedNodeId(null);
  }

  // Keyboard shortcuts (frontend spec §13): Escape clears the selected-node state (closes the
  // Properties Panel back to its empty state); Delete/Backspace removes the selected node —
  // guarded to skip while focused in a text input/textarea (property editing) so deleting a
  // character in a field never gets misread as "delete this node."
  useEffect(() => {
    function handleKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        setSelectedNodeId(null);
        return;
      }
      if (readOnly || !selectedNodeId) return;
      if (e.key !== 'Delete' && e.key !== 'Backspace') return;
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)) return;
      handleDeleteNode(selectedNodeId);
    }
    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [readOnly, selectedNodeId, present]);

  const startCount = present.nodes.filter((n) => n.data.nodeType === 'Start').length;
  const endCount = present.nodes.filter((n) => n.data.nodeType === 'End').length;

  const reactFlowInstanceRef = useRef<ReactFlowInstance<WorkflowFlowNode, WorkflowFlowEdge> | null>(null);

  const decoratedNodes = present.nodes.map((n) => ({
    ...n,
    selected: n.id === selectedNodeId,
    className: mentionedNodeIds.has(n.id) ? 'bpm-node-has-error' : undefined,
  }));

  return (
    <Layout style={{ background: '#fff', border: '1px solid #f0f0f0', borderRadius: 8, overflow: 'hidden' }}>
      <div
        style={{
          padding: '8px 16px',
          borderBottom: '1px solid #f0f0f0',
          display: 'flex',
          justifyContent: 'space-between',
          alignItems: 'center',
          flexWrap: 'wrap',
          gap: 8,
        }}
      >
        <Space wrap>
          {!readOnly && (
            <>
              <Button type="primary" onClick={onSaveDraft} loading={isSaving}>
                {t('common.saveDraft')}
              </Button>
              <Button onClick={onValidate} loading={isValidating}>
                {t('processes.validate')}
              </Button>
              <Button onClick={onPublish} loading={isPublishing} disabled={!canPublish}>
                {t('common.publish')}
              </Button>
              <Button icon={<UndoOutlined />} onClick={undo} disabled={!canUndo}>
                {t('processDesigner.undo')}
              </Button>
              <Button icon={<RedoOutlined />} onClick={redo} disabled={!canRedo}>
                {t('processDesigner.redo')}
              </Button>
            </>
          )}
          <Button icon={<ExpandOutlined />} onClick={() => reactFlowInstanceRef.current?.fitView({ padding: 0.2 })}>
            {t('processDesigner.fitView')}
          </Button>
          {dirty && !readOnly && <Tag color="gold">{t('processes.unsavedChanges')}</Tag>}
          {readOnly && <Tag color="green">{t('processDesigner.publishedReadOnly')}</Tag>}
        </Space>
        <Space size="small">
          <Tag color={startCount === 1 ? 'default' : 'error'}>{t('processDesigner.startNodeCountLabel').replace('{count}', String(startCount))}</Tag>
          <Tag color={endCount >= 1 ? 'default' : 'error'}>{t('processDesigner.endNodeCountLabel').replace('{count}', String(endCount))}</Tag>
        </Space>
      </div>

      <ValidationPanel
        result={validationResult}
        nodes={present.nodes}
        onSelectNode={(nodeId) => setSelectedNodeId(nodeId)}
      />

      <Layout style={{ background: '#fff', minHeight: 520 }}>
        {!readOnly && (
          <Sider width={180} theme="light" style={{ borderInlineEnd: '1px solid #f0f0f0' }}>
            <NodePalette onAddNode={handleAddNode} />
          </Sider>
        )}
        <Content style={{ position: 'relative' }} onDrop={handleDrop} onDragOver={(e) => e.preventDefault()}>
          <ReactFlowProvider>
            <ReactFlow<WorkflowFlowNode, WorkflowFlowEdge>
              nodes={decoratedNodes}
              edges={present.edges}
              nodeTypes={workflowNodeTypes}
              onNodesChange={readOnly ? undefined : handleNodesChange}
              onEdgesChange={readOnly ? undefined : handleEdgesChange}
              onConnect={readOnly ? undefined : handleConnect}
              isValidConnection={isValidConnection}
              nodesDraggable={!readOnly}
              nodesConnectable={!readOnly}
              elementsSelectable
              onNodeClick={(_, node) => setSelectedNodeId(node.id)}
              onPaneClick={() => setSelectedNodeId(null)}
              onInit={(instance) => {
                reactFlowInstanceRef.current = instance;
              }}
              // Delete/Backspace is handled by this component's own keydown listener instead (it
              // needs to skip while a text input/textarea is focused, which React Flow's built-in
              // deleteKeyCode handling doesn't know how to do) — disabling it here avoids the two
              // handlers racing on the same keypress.
              deleteKeyCode={null}
              fitView
              style={{ minHeight: 520 }}
            >
              <Background />
              <Controls />
              <MiniMap />
            </ReactFlow>
          </ReactFlowProvider>
        </Content>
        <Sider width={280} theme="light" style={{ borderInlineStart: '1px solid #f0f0f0' }}>
          <PropertiesPanel
            node={selectedNode}
            readOnly={readOnly}
            onChange={handleNodePropertyChange}
            onDelete={handleDeleteNode}
          />
        </Sider>
      </Layout>
    </Layout>
  );
}
