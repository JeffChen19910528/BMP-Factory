using BPM.Domain.Common;

namespace BPM.Domain.Entities;

public enum FormInstanceStatus
{
    Draft,
    Submitted,
    Locked,
    Cancelled,
}

// Runtime-side entity (Skill.md Phase 4 §11). Pinned to the exact FormVersion it was created
// against — never re-resolved to "whatever is currently published" — mirroring how
// ProcessInstance pins ProcessVersion (Skill.md §2.3).
//
// ProcessInstanceId/TaskInstanceId are both nullable: a FormInstance can be created standalone
// (Skill.md §23 exposes a plain POST /api/form-instances), or the WorkflowEngine can create one
// automatically for a UserTask node that references a form (Skill.md §19) — in that case
// TaskInstanceId is set and the two are 1:1, the same relationship ApprovalInstance has with
// TaskInstance for ApprovalTask nodes.
public class FormInstance : AuditableEntity
{
    public Guid FormDefinitionId { get; set; }
    public FormDefinition? FormDefinition { get; set; }

    public Guid FormVersionId { get; set; }
    public FormVersion? FormVersion { get; set; }

    public Guid? ProcessInstanceId { get; set; }
    public Guid? TaskInstanceId { get; set; }
    public TaskInstance? TaskInstance { get; set; }

    public Guid CreatedByUserId { get; set; }
    public FormInstanceStatus Status { get; set; } = FormInstanceStatus.Draft;

    public FormData? Data { get; set; }
    public ICollection<Attachment> Attachments { get; set; } = new List<Attachment>();
}
