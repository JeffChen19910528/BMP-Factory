using BPM.Domain.Workflow;

namespace BPM.Application.Processes;

// Phase 8 — Version Comparison. Pure, DB-free, in-memory structural diff between two already-
// deserialized WorkflowDefinition graphs — no new persistence, no diff library (Part 17/33: the
// graph is small and this domain's own semantics, especially Sequential-approval assignment
// order, are more precisely expressed by a hand-written comparer than a generic JSON-diff
// package would produce).
//
// Matching is by each node/transition's own stable Id (Part 18/19) — never JSON text/positional
// order. There is no canvas-position/layout field anywhere in WorkflowNodeDefinition (confirmed
// by inspection: the frontend Designer computes layout client-side on every load and never
// persists it — see PROGRESS.md's Phase 8 section) — so there is nothing to explicitly exclude
// from this diff; every field compared here is workflow-semantic by construction.
public static class VersionComparer
{
    public static VersionComparisonResponse Compare(
        Guid processDefinitionId,
        Guid fromVersionId,
        int fromVersionNumber,
        Guid toVersionId,
        int toVersionNumber,
        WorkflowDefinition from,
        WorkflowDefinition to)
    {
        var fromNodes = from.Nodes.ToDictionary(n => n.Id);
        var toNodes = to.Nodes.ToDictionary(n => n.Id);

        var addedNodes = to.Nodes.Where(n => !fromNodes.ContainsKey(n.Id)).Select(ToNodeSummary).ToList();
        var removedNodes = from.Nodes.Where(n => !toNodes.ContainsKey(n.Id)).Select(ToNodeSummary).ToList();
        var modifiedNodes = new List<ModifiedNodeDto>();
        foreach (var fromNode in from.Nodes)
        {
            if (!toNodes.TryGetValue(fromNode.Id, out var toNode))
            {
                continue;
            }
            var changed = GetChangedNodeFields(fromNode, toNode);
            if (changed.Count > 0)
            {
                modifiedNodes.Add(new ModifiedNodeDto(fromNode.Id, fromNode.Name, toNode.Name, changed));
            }
        }

        var fromTransitions = from.Transitions.ToDictionary(t => t.Id);
        var toTransitions = to.Transitions.ToDictionary(t => t.Id);
        var addedTransitions = to.Transitions.Where(t => !fromTransitions.ContainsKey(t.Id)).Select(ToTransitionSummary).ToList();
        var removedTransitions = from.Transitions.Where(t => !toTransitions.ContainsKey(t.Id)).Select(ToTransitionSummary).ToList();
        var modifiedTransitions = new List<ModifiedTransitionDto>();
        foreach (var fromTransition in from.Transitions)
        {
            if (!toTransitions.TryGetValue(fromTransition.Id, out var toTransition))
            {
                continue;
            }
            var changed = GetChangedTransitionFields(fromTransition, toTransition);
            if (changed.Count > 0)
            {
                modifiedTransitions.Add(new ModifiedTransitionDto(fromTransition.Id, changed));
            }
        }

        var totalChanges = addedNodes.Count + removedNodes.Count + modifiedNodes.Count
            + addedTransitions.Count + removedTransitions.Count + modifiedTransitions.Count;
        var summary = totalChanges == 0
            ? "No workflow-level changes detected."
            : $"{addedNodes.Count} node(s) added, {removedNodes.Count} removed, {modifiedNodes.Count} modified; "
              + $"{addedTransitions.Count} transition(s) added, {removedTransitions.Count} removed, {modifiedTransitions.Count} modified.";

        return new VersionComparisonResponse(
            processDefinitionId, fromVersionId, fromVersionNumber, toVersionId, toVersionNumber,
            addedNodes, removedNodes, modifiedNodes, addedTransitions, removedTransitions, modifiedTransitions, summary);
    }

    private static NodeSummaryDto ToNodeSummary(WorkflowNodeDefinition n) => new(n.Id, n.Name, n.Type);

    private static TransitionSummaryDto ToTransitionSummary(WorkflowTransitionDefinition t) => new(t.Id, t.Source, t.Target);

    private static List<string> GetChangedNodeFields(WorkflowNodeDefinition a, WorkflowNodeDefinition b)
    {
        var changed = new List<string>();
        if (a.Type != b.Type) changed.Add("Type");
        if (a.Name != b.Name) changed.Add("Name");
        if (!AssignmentEquals(a.Assignment, b.Assignment)) changed.Add("Assignment");
        if (!ApprovalConfigEquals(a.Approval, b.Approval)) changed.Add("ApprovalConfig");
        if (a.Form?.FormDefinitionKey != b.Form?.FormDefinitionKey) changed.Add("Form");
        return changed;
    }

    private static List<string> GetChangedTransitionFields(WorkflowTransitionDefinition a, WorkflowTransitionDefinition b)
    {
        var changed = new List<string>();
        if (a.Source != b.Source) changed.Add("Source");
        if (a.Target != b.Target) changed.Add("Target");
        if (a.Name != b.Name) changed.Add("Name");
        return changed;
    }

    private static bool AssignmentEquals(WorkflowAssignment? a, WorkflowAssignment? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.Type == b.Type && a.Value == b.Value;
    }

    // Order-sensitive on purpose (Part 20): under a Sequential ApprovalPolicy, Assignments order
    // determines who acts first — this is never treated as an unordered set, unlike a generic
    // JSON-diff tool would default to.
    private static bool ApprovalConfigEquals(ApprovalConfig? a, ApprovalConfig? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        if (a.Policy != b.Policy || a.Assignments.Count != b.Assignments.Count)
        {
            return false;
        }
        for (var i = 0; i < a.Assignments.Count; i++)
        {
            if (!AssignmentEquals(a.Assignments[i], b.Assignments[i]))
            {
                return false;
            }
        }
        return a.AllowReject == b.AllowReject
            && a.AllowReturn == b.AllowReturn
            && a.AllowDelegate == b.AllowDelegate
            && a.AllowTransfer == b.AllowTransfer
            && a.AllowAddApprover == b.AllowAddApprover
            && Equals(a.ReturnPolicy, b.ReturnPolicy); // ReturnPolicy is a plain record (bool + enum) — value-equal already.
    }
}
