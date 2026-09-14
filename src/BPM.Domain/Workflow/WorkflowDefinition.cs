namespace BPM.Domain.Workflow;

// Node types a ProcessVersion.DefinitionJson graph may contain (Skill.md §9). Only Start/End/
// UserTask are executed by the Phase 2 engine; the rest are reserved so the JSON schema and
// validator don't need to change shape again when they're implemented in later phases.
public enum WorkflowNodeType
{
    Start,
    End,
    UserTask,
    ApprovalTask,
    ServiceTask,
    ScriptTask,
    ExclusiveGateway,
    ParallelGateway,
    JoinGateway,
    Timer,
    Notification,
    SubProcess,
}

// Assignment types a UserTask node may specify (Skill.md §10). Only User/Role are resolved by
// the Phase 2 engine; the rest are reserved.
public enum WorkflowAssignmentType
{
    User,
    Role,
    Department,
    Manager,
    DepartmentManager,
    ProcessInitiator,
    Dynamic,
}

public record WorkflowAssignment(WorkflowAssignmentType Type, string Value);

public record WorkflowNodeDefinition(string Id, WorkflowNodeType Type, string Name, WorkflowAssignment? Assignment = null);

public record WorkflowTransitionDefinition(string Id, string Source, string Target, string? Name = null);

// Deserialized form of ProcessVersion.DefinitionJson (Skill.md §11). Plain POCOs deliberately
// live in BPM.Domain (not BPM.Workflow) since Domain may depend on System.Text.Json (BCL) but the
// reverse — Workflow needing Domain types to describe a definition — must hold; keeping this
// shape-only, dependency-free, and framework-agnostic is what makes it safe to live in Domain.
public record WorkflowDefinition(IReadOnlyList<WorkflowNodeDefinition> Nodes, IReadOnlyList<WorkflowTransitionDefinition> Transitions);
