using BPM.Domain.Common;

namespace BPM.Domain.Entities;

// Phase 6.3 — SLA Foundation. Extends AuditableEntity (RowVersion included) deliberately, unlike
// Notification/NotificationDelivery/AuditLog: those are system-generated and never user-edited,
// while an SlaPolicy is admin-configured business data an Administrator can revise — the same
// category as User/Department/Role, which already use this exact optimistic-concurrency
// convention (see UserService/DepartmentService's own ExpectedVersion pattern). SlaPolicyService
// follows that identical pattern for Update.
//
// Scoped to (ProcessDefinitionId, NodeId) rather than embedded in the published
// WorkflowDefinition JSON (Skill.md's graph blob) — chosen after inspecting the Designer/
// serialization architecture: NodeId is a stable, Designer-assigned identifier that persists
// across a process definition's versions (Skill.md §2.3's immutability rule governs the graph
// JSON itself, not this separate table), so "the Manager Approval step in Process A" can carry
// one policy that survives republishing without ever touching the immutable DefinitionJson blob
// or the visual Designer at all. A policy edit only ever affects *future* TaskInstances resolved
// against it (see SlaEngine) — it never rewrites a TaskSla already calculated from it (Part J/X).
public class SlaPolicy : AuditableEntity
{
    public Guid ProcessDefinitionId { get; set; }
    public ProcessDefinition? ProcessDefinition { get; set; }

    // Matches WorkflowNodeDefinition.Id (a string in the graph JSON, not a DB entity — see
    // WorkflowDefinition.cs) — never a foreign key, the same "snapshot reference" relationship
    // TaskInstance.NodeId already has to the graph.
    public string NodeId { get; set; } = string.Empty;

    // A disabled policy simply stops producing new TaskSla rows for future tasks (SlaEngine skips
    // it) — existing TaskSla records already created from it are never touched (Part I/J).
    public bool Enabled { get; set; } = true;

    public int DurationMinutes { get; set; }
    public int WarningOffsetMinutes { get; set; }
}
