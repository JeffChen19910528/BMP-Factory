using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Workflow.Validation;
using Xunit;

namespace BPM.Tests.Workflow;

public class WorkflowDefinitionValidatorTests
{
    private static WorkflowDefinition ValidSequentialDefinition() => new(
        Nodes: new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
            new WorkflowNodeDefinition("approval", WorkflowNodeType.UserTask, "Approval", new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager")),
            new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
        },
        Transitions: new[]
        {
            new WorkflowTransitionDefinition("t1", "start", "approval"),
            new WorkflowTransitionDefinition("t2", "approval", "end"),
        });

    [Fact]
    public void Validate_ValidSequentialDefinition_Passes()
    {
        var result = WorkflowDefinitionValidator.Validate(ValidSequentialDefinition());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_MissingStartNode_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("approval", WorkflowNodeType.UserTask, "Approval", new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager")),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions = new[] { new WorkflowTransitionDefinition("t2", "approval", "end") },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "MISSING_START_NODE");
    }

    [Fact]
    public void Validate_MultipleStartNodes_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("start2", WorkflowNodeType.Start, "Start 2"),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions = new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "end"),
                new WorkflowTransitionDefinition("t2", "start2", "end"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "MULTIPLE_START_NODES");
    }

    [Fact]
    public void Validate_MissingEndNode_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.UserTask, "Approval", new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager")),
            },
            Transitions = new[] { new WorkflowTransitionDefinition("t1", "start", "approval") },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "MISSING_END_NODE");
    }

    [Fact]
    public void Validate_OrphanNode_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
                new WorkflowNodeDefinition("orphan", WorkflowNodeType.UserTask, "Orphan", new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager")),
            },
            Transitions = new[] { new WorkflowTransitionDefinition("t1", "start", "end") },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "ORPHAN_NODE");
    }

    [Fact]
    public void Validate_InvalidTransitionReference_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Transitions = new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "does-not-exist"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_TRANSITION_REFERENCE");
    }

    [Fact]
    public void Validate_DuplicateNodeId_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("start", WorkflowNodeType.UserTask, "Duplicate", new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager")),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "DUPLICATE_NODE_ID");
    }

    [Fact]
    public void Validate_DuplicateTransitionId_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Transitions = new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "approval"),
                new WorkflowTransitionDefinition("t1", "approval", "end"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "DUPLICATE_TRANSITION_ID");
    }

    [Fact]
    public void Validate_UserTaskMissingAssignment_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.UserTask, "Approval"),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "MISSING_ASSIGNMENT");
    }

    [Fact]
    public void Validate_UnreachableNode_Rejected()
    {
        var definition = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
                new WorkflowNodeDefinition("unreachable", WorkflowNodeType.UserTask, "Unreachable", new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager")),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "end"),
                // Keeps 'unreachable' non-orphan (it has an outgoing transition) but nothing
                // points at it from the reachable graph, so it exercises NODE_UNREACHABLE
                // specifically rather than ORPHAN_NODE.
                new WorkflowTransitionDefinition("t2", "unreachable", "end"),
            });

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "NODE_UNREACHABLE");
    }

    [Fact]
    public void Validate_EndNodeWithOutgoingTransition_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Transitions = new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "approval"),
                new WorkflowTransitionDefinition("t2", "approval", "end"),
                new WorkflowTransitionDefinition("t3", "end", "start"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "END_NODE_HAS_OUTGOING_TRANSITION");
    }

    [Fact]
    public void Validate_BranchingWithoutGateway_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("a", WorkflowNodeType.UserTask, "A", new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager")),
                new WorkflowNodeDefinition("b", WorkflowNodeType.UserTask, "B", new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager")),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions = new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "a"),
                new WorkflowTransitionDefinition("t2", "start", "b"),
                new WorkflowTransitionDefinition("t3", "a", "end"),
                new WorkflowTransitionDefinition("t4", "b", "end"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "MULTIPLE_OUTGOING_TRANSITIONS");
    }

    // ---- Phase 3: ApprovalTask ----

    private static WorkflowDefinition ValidApprovalDefinition(ApprovalPolicy policy = ApprovalPolicy.All) => new(
        Nodes: new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
            new WorkflowNodeDefinition("approval", WorkflowNodeType.ApprovalTask, "Approval", Approval: new ApprovalConfig(
                policy,
                new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager") })),
            new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
        },
        Transitions: new[]
        {
            new WorkflowTransitionDefinition("t1", "start", "approval"),
            new WorkflowTransitionDefinition("t2", "approval", "end"),
        });

    [Fact]
    public void Validate_ValidApprovalTaskDefinition_Passes()
    {
        var result = WorkflowDefinitionValidator.Validate(ValidApprovalDefinition());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_ApprovalTaskMissingApprovalConfig_Rejected()
    {
        var definition = ValidApprovalDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.ApprovalTask, "Approval"),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "MISSING_APPROVAL_CONFIG");
    }

    [Fact]
    public void Validate_ApprovalTaskEmptyAssignments_Rejected()
    {
        var definition = ValidApprovalDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.ApprovalTask, "Approval", Approval: new ApprovalConfig(ApprovalPolicy.All, Array.Empty<WorkflowAssignment>())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "EMPTY_APPROVAL_ASSIGNMENTS");
    }

    [Fact]
    public void Validate_ApprovalTaskWithPlainAssignment_Rejected()
    {
        var definition = ValidApprovalDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition(
                    "approval",
                    WorkflowNodeType.ApprovalTask,
                    "Approval",
                    Assignment: new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager"),
                    Approval: new ApprovalConfig(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager") })),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "UNEXPECTED_ASSIGNMENT");
    }

    [Fact]
    public void Validate_UserTaskWithApprovalConfig_Rejected()
    {
        var definition = ValidSequentialDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition(
                    "approval",
                    WorkflowNodeType.UserTask,
                    "Approval",
                    Assignment: new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager"),
                    Approval: new ApprovalConfig(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, "Manager") })),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "UNEXPECTED_APPROVAL_CONFIG");
    }

    [Fact]
    public void Validate_ApprovalTaskDepartmentAssignmentWithNonGuidValue_Rejected()
    {
        var definition = ValidApprovalDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.ApprovalTask, "Approval", Approval: new ApprovalConfig(
                    ApprovalPolicy.All,
                    new[] { new WorkflowAssignment(WorkflowAssignmentType.Department, "not-a-guid") })),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_ASSIGNMENT_VALUE");
    }

    [Fact]
    public void Validate_ApprovalTaskUnsupportedAssignmentType_Rejected()
    {
        var definition = ValidApprovalDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.ApprovalTask, "Approval", Approval: new ApprovalConfig(
                    ApprovalPolicy.All,
                    new[] { new WorkflowAssignment(WorkflowAssignmentType.Manager, "x") })),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "UNSUPPORTED_ASSIGNMENT_TYPE");
    }

    [Fact]
    public void Validate_UserTaskDoesNotSupportDepartmentAssignment_Rejected()
    {
        // Department/DepartmentManager/ProcessInitiator are ApprovalTask-only (Phase 3) — a plain
        // UserTask keeps Phase 2's User/Role-only assignment support.
        var definition = ValidSequentialDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.UserTask, "Approval", new WorkflowAssignment(WorkflowAssignmentType.Department, Guid.NewGuid().ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.Contains(result.Errors, e => e.Code == "UNSUPPORTED_ASSIGNMENT_TYPE");
    }

    [Fact]
    public void Validate_ApprovalTaskProcessInitiatorAssignment_DoesNotRequireValue()
    {
        var definition = ValidApprovalDefinition() with
        {
            Nodes = new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.ApprovalTask, "Approval", Approval: new ApprovalConfig(
                    ApprovalPolicy.AnyOne,
                    new[] { new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, "") })),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
        };

        var result = WorkflowDefinitionValidator.Validate(definition);

        Assert.True(result.IsValid);
    }
}
