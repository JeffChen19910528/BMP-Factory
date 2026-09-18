import { useCallback, useRef, useState } from 'react';

const MAX_HISTORY = 50;

// Generic bounded undo/redo (frontend spec §18: "do not create an unbounded memory-consuming
// history"). A standalone, generic sibling of the Process Designer's
// features/process/designer/useDesignerHistory.ts — deliberately a new file rather than a shared
// import, per this phase's explicit instruction not to touch the existing Process Designer.
// `commit` is called after a meaningful mutation (add/delete/reorder field, a property/option
// change); `setLive` replaces state without pushing history, for in-progress interactions that
// shouldn't each become their own undo step.
export function useHistory<T>(initial: T) {
  const [present, setPresent] = useState<T>(initial);
  const past = useRef<T[]>([]);
  const future = useRef<T[]>([]);
  const [canUndo, setCanUndo] = useState(false);
  const [canRedo, setCanRedo] = useState(false);

  const commit = useCallback(
    (next: T) => {
      past.current = [...past.current, present].slice(-MAX_HISTORY);
      future.current = [];
      setPresent(next);
      setCanUndo(true);
      setCanRedo(false);
    },
    [present],
  );

  const setLive = useCallback((next: T) => {
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

  return { present, commit, setLive, undo, redo, canUndo, canRedo };
}
