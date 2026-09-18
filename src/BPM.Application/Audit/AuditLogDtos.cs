using BPM.Application.Common;

namespace BPM.Application.Audit;

public record AuditLogDto(
    Guid Id,
    Guid? UserId,
    string Action,
    string EntityType,
    string? EntityId,
    string? OldValue,
    string? NewValue,
    DateTime Timestamp);

public record AuditLogQuery(Guid? UserId, string? EntityType, string? EntityId, DateTime? From, DateTime? To, int Page = 1, int PageSize = 50);

public interface IAuditLogQueryService
{
    // Phase 5.5.2: widened from IReadOnlyList<AuditLogDto> to PagedResult<AuditLogDto> — Page/
    // PageSize were already accepted and applied (see AuditLogQueryService), but no TotalCount
    // was ever returned, so no caller could build a real paginated table (know how many pages
    // exist, whether "next" should be enabled) without fetching everything first — exactly the
    // gap PagedResult<T>'s own doc comment already anticipated ("AuditLogQueryService's older
    // page-only pattern... left as-is rather than retrofitted without a caller asking for it").
    // The Administration Audit Logs workspace is that caller. No existing production code or test
    // called this interface before this phase, so this is a zero-blast-radius signature change.
    Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default);
}
