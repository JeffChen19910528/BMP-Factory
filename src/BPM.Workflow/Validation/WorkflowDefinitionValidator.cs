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
    // Node types the engine can actually execute. WorkflowNodeType has more members (reserved
    // for later phases); using one of those today is rejected here rather than accepted and
    // silently ignored at runtime.
    private static readonly HashSet<WorkflowNodeType> SupportedNodeTypes = new()
    {
        WorkflowNodeType.Start,
        WorkflowNodeType.End,
        WorkflowNodeType.UserTask,
        WorkflowNodeType.ApprovalTask,
    };

    // Assignment types a plain UserTask supports. User/Role are unchanged from Phase 2.
    // ProcessInitiator was added in Phase 4 for the canonical "applicant fills out their own
    // request form" pattern (Skill.md Phase 4 §37's acceptance scenario: Start -> Submit Form,
    // assigned back to whoever started the process) — it resolves to exactly one user, so it
    // fits TaskInstance's single AssigneeId model the same way User does. Department/
    // DepartmentManager stay ApprovalTask-only: TaskInstance has no way to represent "any of
    // these department members" the way per-user ApprovalAssignment rows do.
    private static readonly HashSet<WorkflowAssignmentType> SupportedUserTaskAssignmentTypes = new()
    {
        WorkflowAssignmentType.User,
        WorkflowAssignmentType.Role,
        WorkflowAssignmentType.ProcessInitiator,
    };

    // Assignment types an ApprovalTask's assignment list entries support (Phase 3 §6: User/Role/
    // Department/DepartmentManager/ProcessInitiator). Manager and Dynamic are explicitly deferred
    // by §6.
    private static readonly HashSet<WorkflowAssignmentType> SupportedApprovalAssignmentTypes = new()
    {
        WorkflowAssignmentType.User,
        WorkflowAssignmentType.Role,
        WorkflowAssignmentType.Department,
        WorkflowAssignmentType.DepartmentManager,
        WorkflowAssignmentType.ProcessInitiator,
    };

    // Assignment types whose Value is a Guid the engine resolves directly (a user id, or a
    // department id) — validated eagerly so a malformed id fails at publish time, not mid-run.
    private static readonly HashSet<WorkflowAssignmentType> GuidValuedAssignmentTypes = new()
    {
        WorkflowAssignmentType.User,
        WorkflowAssignmentType.Department,
        WorkflowAssignmentType.DepartmentManager,
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
            if (node.Assignment is null)
            {
                result.AddError("MISSING_ASSIGNMENT", $"UserTask node '{node.Id}' must specify an assignment.");
            }
            else
            {
                ValidateAssignment(node.Id, "UserTask", node.Assignment, SupportedUserTaskAssignmentTypes, result);
            }

            if (node.Approval is not null)
            {
                result.AddError("UNEXPECTED_APPROVAL_CONFIG", $"UserTask node '{node.Id}' must not specify an approval configuration; use an ApprovalTask node instead.");
            }
        }

        foreach (var node in definition.Nodes.Where(n => n.Type == WorkflowNodeType.ApprovalTask))
        {
            if (node.Assignment is not null)
            {
                result.AddError("UNEXPECTED_ASSIGNMENT", $"ApprovalTask node '{node.Id}' must not specify a plain assignment; use approval.assignments instead.");
            }

            if (node.Approval is null)
            {
                result.AddError("MISSING_APPROVAL_CONFIG", $"ApprovalTask node '{node.Id}' must specify an approval configuration.");
                continue;
            }

            if (!Enum.IsDefined(node.Approval.Policy))
            {
                result.AddError("INVALID_APPROVAL_POLICY", $"ApprovalTask node '{node.Id}' has an invalid approval policy.");
            }

            if (node.Approval.Assignments.Count == 0)
            {
                result.AddError("EMPTY_APPROVAL_ASSIGNMENTS", $"ApprovalTask node '{node.Id}' must specify at least one approval assignment.");
            }
            else
            {
                foreach (var assignment in node.Approval.Assignments)
                {
                    ValidateAssignment(node.Id, "ApprovalTask", assignment, SupportedApprovalAssignmentTypes, result);
                }
            }
        }
    }

    private static void ValidateAssignment(string nodeId, string nodeKind, WorkflowAssignment assignment, HashSet<WorkflowAssignmentType> supportedTypes, WorkflowValidationResult result)
    {
        // ProcessInitiator needs no configured value — it always resolves to the running
        // instance's initiator — so an empty Value is fine only for that type.
        if (assignment.Type != WorkflowAssignmentType.ProcessInitiator && string.IsNullOrWhiteSpace(assignment.Value))
        {
            result.AddError("MISSING_ASSIGNMENT", $"{nodeKind} node '{nodeId}' assignment of type '{assignment.Type}' must specify a value.");
            return;
        }

        if (!supportedTypes.Contains(assignment.Type))
        {
            result.AddError("UNSUPPORTED_ASSIGNMENT_TYPE", $"{nodeKind} node '{nodeId}' uses assignment type '{assignment.Type}', which is not yet supported.");
            return;
        }

        if (GuidValuedAssignmentTypes.Contains(assignment.Type) && !Guid.TryParse(assignment.Value, out _))
        {
            result.AddError("INVALID_ASSIGNMENT_VALUE", $"{nodeKind} node '{nodeId}' has assignment type '{assignment.Type}' but its value ('{assignment.Value}') is not a valid id.");
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
