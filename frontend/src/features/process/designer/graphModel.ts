import type { Edge, Node } from '@xyflow/react';
import type {
  ApprovalConfig,
  FormReference,
  WorkflowAssignment,
  WorkflowDefinition,
  WorkflowNodeDefinition,
  WorkflowNodeType,
  WorkflowTransitionDefinition,
} from '../../../types/process';

// The designer's node palette (frontend spec §5) — everything else (gateways, Timer,
// ServiceTask, SubProcess, ...) is a *reserved* WorkflowNodeType the backend enum already knows
// about but the engine can't execute yet (see WorkflowDefinitionValidator.SupportedNodeTypes).
// A definition containing one of those is preserved read-only, never edited or discarded here.
export const SUPPORTED_NODE_TYPES: readonly WorkflowNodeType[] = ['Start', 'UserTask', 'ApprovalTask', 'End'];

export function isSupportedNodeType(type: WorkflowNodeType): boolean {
  return (SUPPORTED_NODE_TYPES as readonly string[]).includes(type);
}

// Mirrors WorkflowDefinitionValidator's node-pair rules the frontend spec §7 lists explicitly.
// This is a UI guardrail only — the backend validator remains authoritative; a definition edited
// via the JSON escape hatch can still express things this table forbids, and Validate/Publish
// will judge it, not this table.
const ALLOWED_CONNECTIONS: Record<string, readonly string[]> = {
  Start: ['UserTask', 'ApprovalTask'],
  UserTask: ['UserTask', 'ApprovalTask', 'End'],
  ApprovalTask: ['UserTask', 'ApprovalTask', 'End'],
  End: [],
};

export function isConnectionAllowed(sourceType: WorkflowNodeType, targetType: WorkflowNodeType): boolean {
  return ALLOWED_CONNECTIONS[sourceType]?.includes(targetType) ?? false;
}

export interface WorkflowNodeData extends Record<string, unknown> {
  nodeType: WorkflowNodeType;
  name: string;
  assignment: WorkflowAssignment | null;
  approval: ApprovalConfig | null;
  form: FormReference | null;
  // False for a node type outside SUPPORTED_NODE_TYPES (e.g. loaded from a definition authored
  // via the JSON editor, or by a later phase). Its original WorkflowNodeDefinition is kept in
  // `raw` and round-tripped verbatim — the designer renders it but never edits or drops it
  // (frontend spec §10: "preserve it... clearly mark it as unsupported/read-only").
  supported: boolean;
  raw: WorkflowNodeDefinition;
}

export interface WorkflowEdgeData extends Record<string, unknown> {
  name: string | null;
}

export type WorkflowFlowNode = Node<WorkflowNodeData, 'workflowNode'>;
export type WorkflowFlowEdge = Edge<WorkflowEdgeData>;

export const WORKFLOW_NODE_RENDER_TYPE = 'workflowNode' as const;

function newId(prefix: string): string {
  const random =
    typeof crypto !== 'undefined' && 'randomUUID' in crypto
      ? crypto.randomUUID()
      : Math.random().toString(36).slice(2);
  return `${prefix}-${random}`;
}

export function createNode(nodeType: (typeof SUPPORTED_NODE_TYPES)[number], position: { x: number; y: number }): WorkflowFlowNode {
  const id = newId(nodeType.toLowerCase());
  const raw: WorkflowNodeDefinition = { id, type: nodeType, name: defaultNameFor(nodeType) };
  return {
    id,
    type: WORKFLOW_NODE_RENDER_TYPE,
    position,
    data: {
      nodeType,
      name: raw.name,
      assignment: nodeType === 'UserTask' ? { type: 'Role', value: '' } : null,
      approval: nodeType === 'ApprovalTask' ? defaultApprovalConfig() : null,
      form: null,
      supported: true,
      raw,
    },
  };
}

function defaultNameFor(nodeType: WorkflowNodeType): string {
  switch (nodeType) {
    case 'Start':
      return 'Start';
    case 'End':
      return 'End';
    case 'UserTask':
      return 'User Task';
    case 'ApprovalTask':
      return 'Approval';
    default:
      return nodeType;
  }
}

function defaultApprovalConfig(): ApprovalConfig {
  return {
    policy: 'AnyOne',
    assignments: [],
    allowReject: true,
    allowReturn: true,
    allowDelegate: true,
    allowTransfer: true,
    allowAddApprover: true,
  };
}

export function createEdge(source: string, target: string): WorkflowFlowEdge {
  return {
    id: newId('t'),
    source,
    target,
    data: { name: null },
  };
}

// ---- Deserialize: backend WorkflowDefinition JSON -> React Flow model ----

export function deserializeWorkflowDefinition(definition: WorkflowDefinition): {
  nodes: WorkflowFlowNode[];
  edges: WorkflowFlowEdge[];
} {
  const nodes: WorkflowFlowNode[] = definition.nodes.map((node, index) => ({
    id: node.id,
    type: WORKFLOW_NODE_RENDER_TYPE,
    // Real positions are assigned by layoutNodes() right after this — the backend JSON schema
    // has no position field (see PROGRESS.md's Phase 5.3 architecture note), so this is just a
    // placeholder that spreads nodes out enough that layoutNodes' diffing isn't confused by
    // every node starting at the exact same point.
    position: { x: 0, y: index * 40 },
    data: {
      nodeType: node.type,
      name: node.name,
      assignment: node.assignment ?? null,
      approval: node.approval ?? null,
      form: node.form ?? null,
      supported: isSupportedNodeType(node.type),
      raw: node,
    },
  }));

  const edges: WorkflowFlowEdge[] = definition.transitions.map((transition) => ({
    id: transition.id,
    source: transition.source,
    target: transition.target,
    data: { name: transition.name ?? null },
  }));

  return { nodes: layoutNodes(nodes, edges), edges };
}

// ---- Serialize: React Flow model -> backend WorkflowDefinition JSON ----

export function serializeWorkflowDefinition(nodes: WorkflowFlowNode[], edges: WorkflowFlowEdge[]): WorkflowDefinition {
  return {
    nodes: nodes.map((node): WorkflowNodeDefinition => {
      if (!node.data.supported) {
        // Round-trip verbatim — this designer doesn't understand this node type well enough to
        // safely re-derive it from edited fields (frontend spec §10).
        return node.data.raw;
      }
      return {
        id: node.id,
        type: node.data.nodeType,
        name: node.data.name,
        assignment: node.data.assignment,
        approval: node.data.approval,
        form: node.data.form,
      };
    }),
    transitions: edges.map(
      (edge): WorkflowTransitionDefinition => ({
        id: edge.id,
        source: edge.source,
        target: edge.target,
        name: edge.data?.name,
      }),
    ),
  };
}

// ---- Layout: deterministic layered layout, since the backend stores no position ----

const COLUMN_WIDTH = 260;
const ROW_HEIGHT = 120;

export function layoutNodes(nodes: WorkflowFlowNode[], edges: WorkflowFlowEdge[]): WorkflowFlowNode[] {
  const outgoing = new Map<string, string[]>();
  const hasIncoming = new Set<string>();
  for (const edge of edges) {
    if (!outgoing.has(edge.source)) outgoing.set(edge.source, []);
    outgoing.get(edge.source)!.push(edge.target);
    hasIncoming.add(edge.target);
  }

  const depthById = new Map<string, number>();
  const roots = nodes.filter((n) => n.data.nodeType === 'Start' || !hasIncoming.has(n.id));
  const queue: string[] = [];
  for (const root of roots.length > 0 ? roots : nodes.slice(0, 1)) {
    if (!depthById.has(root.id)) {
      depthById.set(root.id, 0);
      queue.push(root.id);
    }
  }

  while (queue.length > 0) {
    const current = queue.shift()!;
    const currentDepth = depthById.get(current)!;
    for (const next of outgoing.get(current) ?? []) {
      if (!depthById.has(next)) {
        depthById.set(next, currentDepth + 1);
        queue.push(next);
      }
    }
  }

  // Anything BFS never reached (disconnected from every root) still needs a visible spot —
  // append it one column past the deepest reached node rather than dropping it.
  const maxDepth = Math.max(0, ...Array.from(depthById.values()));
  let nextOrphanDepth = maxDepth + 1;
  for (const node of nodes) {
    if (!depthById.has(node.id)) {
      depthById.set(node.id, nextOrphanDepth++);
    }
  }

  const countPerDepth = new Map<number, number>();
  return nodes.map((node) => {
    const depth = depthById.get(node.id) ?? 0;
    const row = countPerDepth.get(depth) ?? 0;
    countPerDepth.set(depth, row + 1);
    return { ...node, position: { x: depth * COLUMN_WIDTH, y: row * ROW_HEIGHT } };
  });
}
