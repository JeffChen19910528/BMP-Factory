import { describe, expect, it } from 'vitest';
import type { WorkflowDefinition } from '../../../types/process';
import {
  createEdge,
  createNode,
  deserializeWorkflowDefinition,
  isConnectionAllowed,
  serializeWorkflowDefinition,
} from './graphModel';

// The most important test in this iteration (frontend spec §20): a workflow authored visually
// must produce backend-compatible JSON without losing semantic information. Compared by content
// (ids/types/names/connections/config), never by array order or JSON string equality — order may
// legitimately differ (e.g. after a delete+recreate) without the workflow's meaning changing.
function sortById<T extends { id: string }>(items: T[]): T[] {
  return [...items].sort((a, b) => a.id.localeCompare(b.id));
}

// An optional field explicitly set to `null` and the same field simply absent deserialize to the
// identical C# object (every optional WorkflowNodeDefinition property defaults to null) — so for
// semantic-equivalence purposes they're the same value, even though a strict deep-equal would
// tell them apart. Drop null-valued keys on both sides before comparing.
function dropNullFields<T extends object>(value: T): T {
  return JSON.parse(JSON.stringify(value, (_key, v) => (v === null ? undefined : v)));
}

function assertSemanticallyEqual(a: WorkflowDefinition, b: WorkflowDefinition) {
  expect(sortById(a.nodes.map(dropNullFields))).toEqual(sortById(b.nodes.map(dropNullFields)));
  expect(sortById(a.transitions.map(dropNullFields))).toEqual(sortById(b.transitions.map(dropNullFields)));
}

const REAL_WORLD_DEFINITION: WorkflowDefinition = {
  nodes: [
    { id: 'start', type: 'Start', name: 'Start' },
    {
      id: 'submit',
      type: 'UserTask',
      name: 'Submit Request',
      assignment: { type: 'ProcessInitiator', value: '' },
      form: { formDefinitionKey: 'purchase-request-form' },
    },
    {
      id: 'approval',
      type: 'ApprovalTask',
      name: 'Manager Approval',
      approval: {
        policy: 'AnyOne',
        assignments: [{ type: 'Role', value: 'Manager' }],
        allowReject: true,
        allowReturn: true,
        allowDelegate: false,
        allowTransfer: true,
        allowAddApprover: true,
      },
    },
    { id: 'end', type: 'End', name: 'End' },
  ],
  transitions: [
    { id: 't1', source: 'start', target: 'submit' },
    { id: 't2', source: 'submit', target: 'approval' },
    { id: 't3', source: 'approval', target: 'end' },
  ],
};

describe('deserializeWorkflowDefinition / serializeWorkflowDefinition round trip', () => {
  it('preserves node ids, types, names, assignment, approval, and form config', () => {
    const { nodes, edges } = deserializeWorkflowDefinition(REAL_WORLD_DEFINITION);
    const roundTripped = serializeWorkflowDefinition(nodes, edges);

    assertSemanticallyEqual(roundTripped, REAL_WORLD_DEFINITION);
  });

  it('preserves connections (transition source/target) exactly', () => {
    const { nodes, edges } = deserializeWorkflowDefinition(REAL_WORLD_DEFINITION);
    const roundTripped = serializeWorkflowDefinition(nodes, edges);

    expect(roundTripped.transitions).toHaveLength(3);
    for (const transition of REAL_WORLD_DEFINITION.transitions) {
      expect(roundTripped.transitions.map(dropNullFields)).toContainEqual(dropNullFields(transition));
    }
  });

  it('assigns every node a real position (the backend has none to load)', () => {
    const { nodes } = deserializeWorkflowDefinition(REAL_WORLD_DEFINITION);
    for (const node of nodes) {
      expect(typeof node.position.x).toBe('number');
      expect(typeof node.position.y).toBe('number');
    }
  });

  it('is semantically stable even when node/edge array order differs after round-tripping', () => {
    const { nodes, edges } = deserializeWorkflowDefinition(REAL_WORLD_DEFINITION);
    // Simulate "delete the last node and recreate it" changing array order without changing
    // the workflow's meaning.
    const reordered = [...nodes.slice(1), nodes[0]];
    const roundTripped = serializeWorkflowDefinition(reordered, edges);

    assertSemanticallyEqual(roundTripped, REAL_WORLD_DEFINITION);
  });

  it('preserves an unsupported node type read-only instead of discarding it', () => {
    const withGateway: WorkflowDefinition = {
      nodes: [
        { id: 'start', type: 'Start', name: 'Start' },
        { id: 'gate', type: 'ExclusiveGateway', name: 'Amount Check' },
        { id: 'end', type: 'End', name: 'End' },
      ],
      transitions: [
        { id: 't1', source: 'start', target: 'gate' },
        { id: 't2', source: 'gate', target: 'end' },
      ],
    };

    const { nodes, edges } = deserializeWorkflowDefinition(withGateway);
    const gatewayNode = nodes.find((n) => n.id === 'gate')!;
    expect(gatewayNode.data.supported).toBe(false);
    expect(gatewayNode.data.nodeType).toBe('ExclusiveGateway');

    const roundTripped = serializeWorkflowDefinition(nodes, edges);
    assertSemanticallyEqual(roundTripped, withGateway);
  });

  it('handles an empty definition without crashing', () => {
    const empty: WorkflowDefinition = { nodes: [], transitions: [] };
    const { nodes, edges } = deserializeWorkflowDefinition(empty);
    expect(nodes).toHaveLength(0);
    expect(serializeWorkflowDefinition(nodes, edges)).toEqual(empty);
  });
});

describe('createNode / createEdge', () => {
  it('never uses an array index as a node id', () => {
    const first = createNode('UserTask', { x: 0, y: 0 });
    const second = createNode('UserTask', { x: 0, y: 0 });
    expect(first.id).not.toBe('0');
    expect(second.id).not.toBe('1');
    expect(first.id).not.toBe(second.id);
  });

  it('gives every new edge a stable, unique id distinct from either endpoint', () => {
    const edge = createEdge('node-a', 'node-b');
    expect(edge.id).not.toBe('node-a');
    expect(edge.id).not.toBe('node-b');
    expect(edge.source).toBe('node-a');
    expect(edge.target).toBe('node-b');
  });
});

describe('isConnectionAllowed', () => {
  it.each([
    ['Start', 'UserTask', true],
    ['Start', 'ApprovalTask', true],
    ['UserTask', 'UserTask', true],
    ['UserTask', 'ApprovalTask', true],
    ['UserTask', 'End', true],
    ['ApprovalTask', 'UserTask', true],
    ['ApprovalTask', 'ApprovalTask', true],
    ['ApprovalTask', 'End', true],
  ] as const)('allows %s -> %s', (source, target, expected) => {
    expect(isConnectionAllowed(source, target)).toBe(expected);
  });

  it.each([
    ['End', 'Start', false],
    ['End', 'UserTask', false],
    ['End', 'End', false],
    ['Start', 'Start', false],
    ['Start', 'End', false],
  ] as const)('rejects %s -> %s', (source, target, expected) => {
    expect(isConnectionAllowed(source, target)).toBe(expected);
  });
});
