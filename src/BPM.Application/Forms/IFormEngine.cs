namespace BPM.Application.Forms;

// Mutating form operations — the Form Engine equivalent of IWorkflowEngine (Skill.md Phase 4,
// mirroring the Workflow/Approval Engine split from Phase 2/3): validates and publishes a
// FormVersion, and manages a FormInstance's lifecycle (Draft -> Submitted[-> Locked] / Cancelled).
// The concrete implementation is the only place allowed to mutate FormInstance/FormData together
// with the WorkflowEngine's transition logic when a submission completes a linked task.
public interface IFormEngine
{
    Task<FormVersionDto> PublishVersionAsync(Guid formDefinitionId, Guid publishedBy, CancellationToken cancellationToken = default);

    // Manual/standalone creation (Skill.md §23's plain POST /api/form-instances). The
    // WorkflowEngine creates FormInstances tied to a TaskInstance itself, not through this method.
    Task<FormInstanceDto> CreateInstanceAsync(CreateFormInstanceRequest request, Guid currentUserId, CancellationToken cancellationToken = default);

    // Throws ConflictAppException (FORM_CONCURRENCY_CONFLICT) if request.ExpectedVersion doesn't
    // match the FormData row's current RowVersion (Skill.md §26), and ValidationAppException if
    // the submitted values fail the FormVersion's schema (Skill.md §14).
    Task<FormDataDto> SaveDataAsync(Guid formInstanceId, UpdateFormDataRequest request, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    // Validates the current data against the schema (all Required fields present, etc.), flips
    // Draft -> Submitted, and — if this instance is tied to a still-open UserTask — completes
    // that task and advances the workflow in the same transaction, then flips to Locked
    // (Skill.md §18: "a submitted/locked form must not be freely editable").
    Task<FormInstanceDto> SubmitAsync(Guid formInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);

    Task<FormInstanceDto> CancelAsync(Guid formInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default);
}
