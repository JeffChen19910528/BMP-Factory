using BPM.Application.Common;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;

namespace BPM.Application.Processes;

public record ProcessDefinitionDto(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string? Category,
    ProcessDefinitionStatus Status,
    Guid? CurrentVersionId,
    DateTime CreatedAt,
    Guid? CreatedBy,
    DateTime? UpdatedAt,
    // Phase 8 — governance ownership; NULL means no owner has been assigned, in which case only
    // an Administrator (never an "owner") may perform owner-gated governance actions.
    Guid? OwnerUserId,
    // Base64-encoded RowVersion, same convention as ProcessVersionDto.RowVersion below — echo
    // back as ExpectedVersion on Suspend/Archive/Restore/AssignOwner.
    string RowVersion);

public record CreateProcessDefinitionRequest(string Key, string Name, string? Description, string? Category);

// Metadata-only edit (Phase 5.2 §6's "[Edit]" action on a Draft process) — Key is deliberately
// excluded: it's the stable identifier StartProcessAsync resolves by, never meant to change after
// creation. Unlike ProcessVersion.DefinitionJson, this doesn't touch anything Skill.md §2.3 calls
// immutable, so it's allowed regardless of the definition's Status — the frontend only surfaces
// the [Edit] action for Draft to match the spec's worked example, but that's a UI choice, not a
// backend restriction.
public record UpdateProcessDefinitionRequest(string Name, string? Description, string? Category);

// Page/PageSize follow the same convention as BPM.Application.Audit.AuditLogQuery. Search matches
// Key or Name (case-insensitive, substring) — there's no full-text index, this is a small admin
// tool's list filter, not a search product.
public record ProcessDefinitionQuery(string? Search = null, ProcessDefinitionStatus? Status = null, int Page = 1, int PageSize = 20);

public record ProcessVersionDto(
    Guid Id,
    Guid ProcessDefinitionId,
    int VersionNumber,
    ProcessVersionStatus Status,
    WorkflowDefinition Definition,
    DateTime CreatedAt,
    Guid? CreatedBy,
    DateTime? PublishedAt,
    Guid? PublishedBy,
    // Phase 8 — optional governance note supplied at publish time; NULL for pre-Phase-8 versions
    // and any publish where the caller didn't supply one. Immutable once set, like the rest of a
    // Published version.
    string? ChangeReason,
    // Base64-encoded AuditableEntity.RowVersion (Skill.md §33), same convention as
    // FormDataDto.Version — a client must echo this back as UpdateProcessVersionRequest's
    // ExpectedVersion to save; a stale one is rejected with 409 PROCESS_VERSION_CONCURRENCY_CONFLICT
    // rather than silently overwriting a concurrent edit (Phase 5.3.2: this was a real gap —
    // UpdateVersionAsync previously had no concurrency check at all, unlike every other mutating
    // endpoint in the codebase).
    string RowVersion);

public record CreateProcessVersionRequest(WorkflowDefinition Definition);

// Save Draft (Phase 5.2 §9) — updates an existing Draft version's DefinitionJson in place.
// Rejected with ConflictAppException if the target version is already Published (Skill.md §2.3:
// published versions are immutable) — this is a distinct operation from CreateVersionAsync, which
// always creates a new version row. ExpectedVersion is the RowVersion the client last read
// (ProcessVersionDto.RowVersion) — required so two people editing the same draft concurrently
// can't silently clobber each other (see ProcessVersionDto.RowVersion's comment).
public record UpdateProcessVersionRequest(WorkflowDefinition Definition, string ExpectedVersion);

// Phase 8 — Process Governance & Lifecycle. One shared request shape for every lifecycle
// transition (Suspend/Archive/Restore) — all three are "no other input needed beyond proving you
// last read the current RowVersion," so one record covers all three rather than three near-
// identical ones.
public record ProcessLifecycleActionRequest(string ExpectedVersion);

// OwnerUserId is nullable so an Administrator can also *clear* an owner (Part 4: "clear owner if
// appropriate") by passing null — never inferred, never defaulted to CreatedBy.
public record AssignProcessOwnerRequest(Guid? OwnerUserId, string ExpectedVersion);

public record NodeSummaryDto(string NodeId, string Name, WorkflowNodeType Type);

public record ModifiedNodeDto(string NodeId, string FromName, string ToName, IReadOnlyList<string> ChangedFields);

public record TransitionSummaryDto(string TransitionId, string Source, string Target);

public record ModifiedTransitionDto(string TransitionId, IReadOnlyList<string> ChangedFields);

// Phase 8 — Version Comparison. A pure, on-demand structural diff between two ProcessVersion
// DefinitionJson documents belonging to the SAME ProcessDefinition (enforced server-side, never
// trusted from the request — see ProcessDefinitionService.CompareVersionsAsync). Nodes/transitions
// are matched by their own stable Id (never positional/JSON-text order), and there is no persisted
// canvas-position field anywhere in WorkflowDefinition for this comparison to need to exclude —
// see ProcessDefinitionService's own comment on this. Two semantically identical versions produce
// a valid, empty comparison (Summary explains this) rather than an error.
public record VersionComparisonResponse(
    Guid ProcessDefinitionId,
    Guid FromVersionId,
    int FromVersionNumber,
    Guid ToVersionId,
    int ToVersionNumber,
    IReadOnlyList<NodeSummaryDto> AddedNodes,
    IReadOnlyList<NodeSummaryDto> RemovedNodes,
    IReadOnlyList<ModifiedNodeDto> ModifiedNodes,
    IReadOnlyList<TransitionSummaryDto> AddedTransitions,
    IReadOnlyList<TransitionSummaryDto> RemovedTransitions,
    IReadOnlyList<ModifiedTransitionDto> ModifiedTransitions,
    string Summary);

public interface IProcessDefinitionService
{
    Task<PagedResult<ProcessDefinitionDto>> GetAllAsync(ProcessDefinitionQuery query, CancellationToken cancellationToken = default);
    Task<ProcessDefinitionDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ProcessDefinitionDto> CreateAsync(CreateProcessDefinitionRequest request, CancellationToken cancellationToken = default);
    Task<ProcessDefinitionDto> UpdateAsync(Guid id, UpdateProcessDefinitionRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProcessVersionDto>> GetVersionsAsync(Guid processDefinitionId, CancellationToken cancellationToken = default);
    Task<ProcessVersionDto> CreateVersionAsync(Guid processDefinitionId, CreateProcessVersionRequest request, CancellationToken cancellationToken = default);
    Task<ProcessVersionDto> UpdateVersionAsync(Guid processDefinitionId, Guid versionId, UpdateProcessVersionRequest request, CancellationToken cancellationToken = default);

    // Phase 8 — Process Governance & Lifecycle. currentUserId/currentUserRoles come from the
    // authenticated caller (ICurrentUserService, via the controller) — Suspend/Archive/Restore
    // are authorized as Administrator-OR-owner (computed from the loaded entity's own
    // OwnerUserId, never trusted from the request); AssignOwnerAsync remains Administrator-only
    // (enforced by the controller's own [Authorize(Roles="Administrator")], same as
    // Create/Update/Publish).
    Task<ProcessDefinitionDto> SuspendAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ProcessLifecycleActionRequest request, CancellationToken cancellationToken = default);
    Task<ProcessDefinitionDto> ArchiveAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ProcessLifecycleActionRequest request, CancellationToken cancellationToken = default);
    Task<ProcessDefinitionDto> RestoreAsync(Guid id, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, ProcessLifecycleActionRequest request, CancellationToken cancellationToken = default);
    Task<ProcessDefinitionDto> AssignOwnerAsync(Guid id, AssignProcessOwnerRequest request, CancellationToken cancellationToken = default);

    Task<VersionComparisonResponse> CompareVersionsAsync(Guid processDefinitionId, Guid fromVersionId, Guid toVersionId, CancellationToken cancellationToken = default);
}
