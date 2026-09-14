using BPM.Domain.Workflow;

namespace BPM.Workflow.Validation;

// Publish-time validation (Skill.md §12, §30). Pure and DB-free by design — it operates only on
// the already-deserialized WorkflowDefinition, so it's trivially unit-testable and reusable for
// import validation later (Skill.md §42) without touching persistence. Fails closed: any
// violation blocks publish.
//
// Beyond the literal §12 checklist this also enforces a Phase 2 structural constraint — every
// non-End node must have exactly one outgoing transition, and End nodes must have none — because
// there is no gateway node type executable yet (Skill.md §28 explicitly defers
// ExclusiveGateway/ParallelGateway to a later phase). Without this rule a definition could
// describe branching the engine has no way to execute.
public static class WorkflowDefinitionValidator
{
    // Node types the Phase 2 engine can actually execute. WorkflowNodeType has more members
    // (reserved for later phases); using one of those today is rejected here rather than
    // accepted and silently ignored at runtime.
    private static readonly HashSet<WorkflowNodeType> SupportedNodeTypes = new()
    {
        WorkflowNodeType.Start,
        WorkflowNodeType.End,
        WorkflowNodeType.UserTask,
    };

    private static readonly HashSet<WorkflowAssignmentType> SupportedAssignmentTypes = new()
    {
        WorkflowAssignmentType.User,
        WorkflowAssignmentType.Role,
    };

    public static WorkflowValidationResult Validate(WorkflowDefinition? definition)
    {
        var result = new WorkflowValidationResult();

        if (definition is null || definition.Nodes.Count == 0)
        {
            result.AddError("EMPTY_DEFINITION", "Workflow definition must contain at least one node.");
            return result;
        }

        ValidateNodeIds(definition, result);
        ValidateTransitionIds(definition, result);
        ValidateTransitionReferences(definition, result);
        ValidateNodeTypes(definition, result);
        ValidateAssignments(definition, result);

        // Reachability/outgoing-count checks assume node IDs and transition references are
        // already sound — skip them if the structure is too broken to reason about safely.
        if (result.IsValid)
        {
            ValidateStartAndEnd(definition, result);
            ValidateOutgoingCounts(definition, result);
            ValidateOrphansAndReachability(definition, result);
        }

        return result;
    }

    private static void ValidateNodeIds(WorkflowDefinition definition, WorkflowValidationResult result)
    {
        var seen = new HashSet<string>();
        foreach (var node in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                result.AddError("INVALID_NODE_ID", "Every node must have a non-empty id.");
            }
            else if (!seen.Add(node.Id))
            {
                result.AddError("DUPLICATE_NODE_ID", $"Node id '{node.Id}' is used more than once.");
            }
        }
    }

    private static void ValidateTransitionIds(WorkflowDefinition definition, WorkflowValidationResult result)
    {
        var seen = new HashSet<string>();
        foreach (var transition in definition.Transitions)
        {
            if (string.IsNullOrWhiteSpace(transition.Id))
            {
                result.AddError("INVALID_TRANSITION_ID", "Every transition must have a non-empty id.");
            }
            else if (!seen.Add(transition.Id))
            {
                result.AddError("DUPLICATE_TRANSITION_ID", $"Transition id '{transition.Id}' is used more than once.");
            }
        }
    }

    private static void ValidateTransitionReferences(WorkflowDefinition definition, WorkflowValidationResult result)
    {
        var nodeIds = definition.Nodes.Select(n => n.Id).ToHashSet();
        foreach (var transition in definition.Transitions)
        {
            if (!nodeIds.Contains(transition.Source))
            {
                result.AddError("INVALID_TRANSITION_REFERENCE", $"Transition '{transition.Id}' references unknown source node '{transition.Source}'.");
            }
            if (!nodeIds.Contains(transition.Target))
            {
                result.AddError("INVALID_TRANSITION_REFERENCE", $"Transition '{transition.Id}' references unknown target node '{transition.Target}'.");
            }
        }
    }

    private static void ValidateNodeTypes(WorkflowDefinition definition, WorkflowValidationResult result)
    {
        foreach (var node in definition.Nodes)
        {
            if (!SupportedNodeTypes.Contains(node.Type))
            {
                result.AddError("UNSUPPORTED_NODE_TYPE", $"Node '{node.Id}' has type '{node.Type}', which is not yet executable by the workflow engine.");
            }
        }
    }

    private static void ValidateAssignments(WorkflowDefinition definition, WorkflowValidationResult result)
    {
        foreach (var node in definition.Nodes.Where(n => n.Type == WorkflowNodeType.UserTask))
        {
            if (node.Assignment is null || string.IsNullOrWhiteSpace(node.Assignment.Value))
            {
                result.AddError("MISSING_ASSIGNMENT", $"UserTask node '{node.Id}' must specify an assignment.");
            }
            else if (!SupportedAssignmentTypes.Contains(node.Assignment.Type))
            {
                result.AddError("UNSUPPORTED_ASSIGNMENT_TYPE", $"UserTask node '{node.Id}' uses assignment type '{node.Assignment.Type}', which is not yet supported.");
            }
            else if (node.Assignment.Type == WorkflowAssignmentType.User && !Guid.TryParse(node.Assignment.Value, out _))
            {
                result.AddError("INVALID_ASSIGNMENT_VALUE", $"UserTask node '{node.Id}' has assignment type 'User' but its value ('{node.Assignment.Value}') is not a valid user id.");
            }
        }
    }

    private static void ValidateStartAndEnd(WorkflowDefinition definition, WorkflowValidationResult result)
    {
        var startCount = definition.Nodes.Count(n => n.Type == WorkflowNodeType.Start);
        if (startCount == 0)
        {
            result.AddError("MISSING_START_NODE", "Workflow must contain exactly one start node.");
        }
        else if (startCount > 1)
        {
            result.AddError("MULTIPLE_START_NODES", "Workflow must contain exactly one start node.");
        }

        if (definition.Nodes.All(n => n.Type != WorkflowNodeType.End))
        {
            result.AddError("MISSING_END_NODE", "Workflow must contain at least one end node.");
        }
    }

    private static void ValidateOutgoingCounts(WorkflowDefinition definition, WorkflowValidationResult result)
    {
        var outgoingCounts = definition.Transitions
            .GroupBy(t => t.Source)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var node in definition.Nodes)
        {
            var outgoing = outgoingCounts.GetValueOrDefault(node.Id, 0);

            if (node.Type == WorkflowNodeType.End)
            {
                if (outgoing > 0)
                {
                    result.AddError("END_NODE_HAS_OUTGOING_TRANSITION", $"End node '{node.Id}' must not have any outgoing transitions.");
                }
            }
            else if (outgoing == 0)
            {
                result.AddError("MISSING_OUTGOING_TRANSITION", $"Node '{node.Id}' must have exactly one outgoing transition.");
            }
            else if (outgoing > 1)
            {
                result.AddError("MULTIPLE_OUTGOING_TRANSITIONS", $"Node '{node.Id}' has {outgoing} outgoing transitions; branching requires a gateway node, which is not yet supported.");
            }
        }
    }

    private static void ValidateOrphansAndReachability(WorkflowDefinition definition, WorkflowValidationResult result)
    {
        var referenced = new HashSet<string>();
        foreach (var transition in definition.Transitions)
        {
            referenced.Add(transition.Source);
            referenced.Add(transition.Target);
        }

        foreach (var node in definition.Nodes.Where(n => !referenced.Contains(n.Id)))
        {
            result.AddError("ORPHAN_NODE", $"Node '{node.Id}' is not connected to any transition.");
        }

        var startNode = definition.Nodes.FirstOrDefault(n => n.Type == WorkflowNodeType.Start);
        if (startNode is null)
        {
            return;
        }

        var outgoingByNode = definition.Transitions
            .GroupBy(t => t.Source)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Target).ToList());

        var visited = new HashSet<string> { startNode.Id };
        var queue = new Queue<string>();
        queue.Enqueue(startNode.Id);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outgoingByNode.TryGetValue(current, out var targets))
            {
                continue;
            }
            foreach (var target in targets.Where(visited.Add))
            {
                queue.Enqueue(target);
            }
        }

        foreach (var node in definition.Nodes.Where(n => !visited.Contains(n.Id)))
        {
            result.AddError("NODE_UNREACHABLE", $"Node '{node.Id}' is not reachable from the start node.");
        }

        if (definition.Nodes.Where(n => n.Type == WorkflowNodeType.End).All(n => !visited.Contains(n.Id)))
        {
            result.AddError("END_NODE_UNREACHABLE", "No end node is reachable from the start node.");
        }
    }
}
