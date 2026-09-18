using BPM.Domain.Common;
using BPM.Domain.Workflow;

namespace BPM.Domain.Entities;

// Phase 6.4 — SLA Scheduler / Escalation. Minimal escalation concept (Part P): "when a Task's SLA
// has been Overdue for a configured delay, notify one additional target." Extends AuditableEntity
// exactly like SlaPolicy (admin-configured business config, same RowVersion/ExpectedVersion
// concurrency pattern) — not a system-generated record.
//
// Scoped to (ProcessDefinitionId, NodeId), same key shape as SlaPolicy and for the same reason —
// escalation only ever matters together with an SLA on that node (there is no "Overdue" without an
// SlaPolicy already producing a TaskSla for it), but the two are deliberately separate tables
// rather than columns bolted onto SlaPolicy: SLA duration/warning and escalation delay/target are
// different administrative concerns that can be configured, enabled, and audited independently.
// No FK/existence check against SlaPolicy is enforced — the same loose-coupling precedent TaskSla
// already has to SlaPolicy (a policy can be deleted/disabled independently without orphaning this
// row in an unsafe way; SlaProcessor simply finds no match and never escalates).
//
// TargetType/TargetValue deliberately reuse WorkflowAssignmentType/the WorkflowAssignment shape
// (Value as a string) rather than inventing a second recipient model — resolved by the exact same
// AssignmentResolver.ResolveAsync the workflow engine already uses for node assignment (Part T: "do
// not create a new recipient resolver"). Only User/Role/DepartmentManager/ProcessInitiator are
// valid here (see CreateEscalationPolicyRequestValidator) — Department is excluded because
// escalating to "everyone in a department" has no clear single-recipient business meaning for an
// overdue-task alert, unlike DepartmentManager.
public class EscalationPolicy : AuditableEntity
{
    public Guid ProcessDefinitionId { get; set; }
    public ProcessDefinition? ProcessDefinition { get; set; }

    public string NodeId { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    // How long after OverdueAt escalation should fire (Part Q: "DueAt -> Overdue -> EscalationAt ->
    // Escalation", not immediate-on-overdue unless a delay of 0 is explicitly configured).
    public int DelayMinutes { get; set; }

    public WorkflowAssignmentType TargetType { get; set; }
    public string TargetValue { get; set; } = string.Empty;
}
