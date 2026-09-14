using BPM.Domain.Entities;

namespace BPM.Application.Forms;

// Shared identity check for "does this person have a legitimate connection to this
// FormInstance" (Skill.md Phase 4 §21/§22) — used by both the read-only query service
// (BPM.Infrastructure) and FormEngine's mutating actions (BPM.Workflow), so the two never drift
// out of sync on who's allowed to see or touch a form. Deliberately identity-only: callers still
// decide lifecycle rules (e.g. "only while Draft") on top of this.
public interface IFormAuthorizationService
{
    // Creator, or the assignee (direct or role-holder) of the task this instance is attached to.
    Task<bool> CanEditAsync(FormInstance instance, Guid userId, IReadOnlyCollection<string> userRoles, CancellationToken cancellationToken = default);

    // Everything CanEdit allows, plus: the process's initiator, and anyone holding (or delegated)
    // an ApprovalAssignment on any task belonging to the same ProcessInstance (Skill.md §20/§21:
    // "Approvers should be able to read the relevant FormInstance").
    Task<bool> CanViewAsync(FormInstance instance, Guid userId, IReadOnlyCollection<string> userRoles, CancellationToken cancellationToken = default);
}
