using BPM.Domain.Entities;

namespace BPM.Domain.Workflow;

// Node types a ProcessVersion.DefinitionJson graph may contain (Skill.md §9). Start/End/UserTask/
// ApprovalTask are executed by the engine as of Phase 3; the rest are reserved so the JSON schema
// and validator don't need to change shape again when they're implemented in later phases.
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

// Assignment types a node may specify (Skill.md §10, Phase 3 §6). User/Role/Department/
// DepartmentManager/ProcessInitiator are resolved by the engine as of Phase 3; Manager/Dynamic
// are reserved (Phase 3 §6 explicitly defers Manager to a later phase, distinct from
// DepartmentManager which is in scope now).
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

// Which node to send a Returned task back to (Skill.md §16). PreviousUserTask is the only
// supported target as of Phase 3; SpecificNode/Requester/PreviousApproval are reserved.
public enum ReturnTarget
{
    PreviousUserTask,
}

public record ReturnPolicy(bool Enabled, ReturnTarget Target = ReturnTarget.PreviousUserTask);

// ApprovalTask-only configuration (Skill.md Phase 3 §28). Assignments is deliberately a list
// (not the single WorkflowAssignment a UserTask uses): under All/Sequential every resolved user
// across every entry is required, and under AnyOne the union is the candidate pool — this is what
// lets one node express "Finance AND Legal AND IT must each approve" without a distinct node per
// role (see PROGRESS.md's Phase 3 section for the acceptance-scenario reasoning).
public record ApprovalConfig(
    ApprovalPolicy Policy,
    IReadOnlyList<WorkflowAssignment> Assignments,
    bool AllowReject = true,
    bool AllowReturn = true,
    bool AllowDelegate = true,
    bool AllowTransfer = true,
    bool AllowAddApprover = true,
    ReturnPolicy? ReturnPolicy = null);

public record WorkflowNodeDefinition(
    string Id,
    WorkflowNodeType Type,
    string Name,
    WorkflowAssignment? Assignment = null,
    ApprovalConfig? Approval = null);

public record WorkflowTransitionDefinition(string Id, string Source, string Target, string? Name = null);

// Deserialized form of ProcessVersion.DefinitionJson (Skill.md §11). Plain POCOs deliberately
// live in BPM.Domain (not BPM.Workflow) since Domain may depend on System.Text.Json (BCL) but the
// reverse — Workflow needing Domain types to describe a definition — must hold; keeping this
// shape-only, dependency-free, and framework-agnostic is what makes it safe to live in Domain.
public record WorkflowDefinition(IReadOnlyList<WorkflowNodeDefinition> Nodes, IReadOnlyList<WorkflowTransitionDefinition> Transitions);
