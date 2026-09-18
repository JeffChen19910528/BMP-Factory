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

// Optional form binding for a UserTask node (Skill.md Phase 4 §19). Authored by key only — the
// Designer never picks a specific FormVersion, matching how a workflow author never picks a
// specific ProcessVersion either.
//
// Phase 10 — FormVersionId (nullable) is snapshotted server-side by WorkflowEngine.PublishVersionAsync
// at the moment a ProcessVersion is published: whichever FormVersion is currently published for
// FormDefinitionKey at that instant is written into this field and the graph is re-serialized
// before the ProcessVersion's own DefinitionJson is frozen. From then on every task created for
// this node, for the life of this ProcessVersion — across every ProcessInstance that ever runs on
// it, and every re-entry via Return — resolves that exact FormVersion, never a live lookup of
// FormDefinition.CurrentVersionId. This is what makes Form pinning as deterministic as Workflow's
// own ProcessVersion pinning, closing the one real historical-consistency gap Phase 10's own
// Architecture Discovery found (a node's form could previously change out from under an
// already-published, supposedly-immutable ProcessVersion if the form was simply republished).
//
// A GUID embedded inside DefinitionJson is not a new pattern here — WorkflowAssignment.Value
// already stores a raw User/Department GUID string for those assignment types; this follows the
// same precedent rather than introducing a new database column.
//
// FormVersionId is null on every Draft version (resolution is a publish-time concern, invisible
// to Designer authoring) and stays null forever on any ProcessVersion published before this
// field existed — FormEngine treats null as "no pin recorded" and falls back to its original,
// pre-Phase-10 live-lookup behavior for those older rows, so no historical ProcessVersion's
// actual runtime behavior is retroactively changed by this field's mere existence.
public record FormReference(string FormDefinitionKey, Guid? FormVersionId = null);

public record WorkflowNodeDefinition(
    string Id,
    WorkflowNodeType Type,
    string Name,
    WorkflowAssignment? Assignment = null,
    ApprovalConfig? Approval = null,
    FormReference? Form = null);

public record WorkflowTransitionDefinition(string Id, string Source, string Target, string? Name = null);

// Deserialized form of ProcessVersion.DefinitionJson (Skill.md §11). Plain POCOs deliberately
// live in BPM.Domain (not BPM.Workflow) since Domain may depend on System.Text.Json (BCL) but the
// reverse — Workflow needing Domain types to describe a definition — must hold; keeping this
// shape-only, dependency-free, and framework-agnostic is what makes it safe to live in Domain.
public record WorkflowDefinition(IReadOnlyList<WorkflowNodeDefinition> Nodes, IReadOnlyList<WorkflowTransitionDefinition> Transitions);
