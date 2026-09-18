using BPM.Domain.Entities;

namespace BPM.Application.Sla;

public record SlaPolicyDto(
    Guid Id,
    Guid ProcessDefinitionId,
    string NodeId,
    bool Enabled,
    int DurationMinutes,
    int WarningOffsetMinutes,
    string RowVersion);

public record CreateSlaPolicyRequest(Guid ProcessDefinitionId, string NodeId, bool Enabled, int DurationMinutes, int WarningOffsetMinutes);

public record UpdateSlaPolicyRequest(bool Enabled, int DurationMinutes, int WarningOffsetMinutes, string ExpectedVersion);

// Phase 6.3 — minimal backend/API-level configuration only (Part R: no policy designer, no
// calendar/escalation UI). CRUD follows the exact same RowVersion/ExpectedVersion optimistic-
// concurrency pattern already used for User/Department/ProcessVersion/FormVersion — an
// Administrator-editable business record, not a system-generated one (see SlaPolicy.cs's own
// comment on why it extends AuditableEntity, unlike Notification/TaskSla).
public interface ISlaPolicyService
{
    Task<IReadOnlyList<SlaPolicyDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<SlaPolicyDto> CreateAsync(CreateSlaPolicyRequest request, CancellationToken cancellationToken = default);
    Task<SlaPolicyDto?> UpdateAsync(Guid id, UpdateSlaPolicyRequest request, CancellationToken cancellationToken = default);
}

// Phase 6.3 — rides along on the existing, already-authorized TaskDto (Part P/Q: reuse existing
// Task authorization rather than build a second one) instead of a standalone SLA endpoint. No
// PolicyId/PolicyDetails here — a Task Detail viewer needs "when is this due," not the
// administrative policy internals (Part P: "不要暴露... policy internals unnecessarily").
public record TaskSlaDto(
    DateTime StartedAt,
    DateTime WarningAt,
    DateTime DueAt,
    DateTime? CompletedAt,
    TaskSlaStatus Status);
