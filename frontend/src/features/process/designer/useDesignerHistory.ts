import { useCallback, useRef, useState } from 'react';
import type { WorkflowFlowEdge, WorkflowFlowNode } from './graphModel';

export interface DesignerGraphState {
  nodes: WorkflowFlowNode[];
  edges: WorkflowFlowEdge[];
}

const MAX_HISTORY = 50;

// Bounded undo/redo (frontend spec §16: "reasonable bounded history is acceptable") over plain
// {nodes, edges} snapshots — cheap to store since a workflow graph is small, and simpler than
// diff-based undo for a first iteration. `commit` is called explicitly after a meaningful
// mutation (add/delete node, add/delete edge, drag-stop, a property edit) rather than on every
// intermediate render, so dragging a node doesn't flood the stack with one entry per pixel.
export function useDesignerHistory(initial: DesignerGraphState) {
  const [present, setPresent] = useState<DesignerGraphState>(initial);
  const past = useRef<DesignerGraphState[]>([]);
  const future = useRef<DesignerGraphState[]>([]);
  const [canUndo, setCanUndo] = useState(false);
  const [canRedo, setCanRedo] = useState(false);

  const commit = useCallback((next: DesignerGraphState) => {
    past.current = [...past.current, present].slice(-MAX_HISTORY);
    future.current = [];
    setPresent(next);
    setCanUndo(true);
    setCanRedo(false);
  }, [present]);

  // Replaces the live state without pushing history — used for in-progress interactions (e.g.
  // node dragging while the mouse button is still down) where each intermediate frame shouldn't
  // become its own undo step.
  const setLive = useCallback((next: DesignerGraphState) => {
    setPresent(next);
  }, []);

  const undo = useCallback(() => {
    setPresent((current) => {
      if (past.current.length === 0) return current;
      const previous = past.current[past.current.length - 1];
      past.current = past.current.slice(0, -1);
      future.current = [current, ...future.current].slice(0, MAX_HISTORY);
      setCanUndo(past.current.length > 0);
      setCanRedo(true);
      return previous;
    });
  }, []);

  const redo = useCallback(() => {
    setPresent((current) => {
      if (future.current.length === 0) return current;
      const next = future.current[0];
      future.current = future.current.slice(1);
      past.current = [...past.current, current].slice(-MAX_HISTORY);
      setCanRedo(future.current.length > 0);
      setCanUndo(true);
      return next;
    });
  }, []);

  const reset = useCallback((next: DesignerGraphState) => {
    past.current = [];
    future.current = [];
    setPresent(next);
    setCanUndo(false);
    setCanRedo(false);
  }, []);

  return { present, commit, setLive, undo, redo, reset, canUndo, canRedo };
}
