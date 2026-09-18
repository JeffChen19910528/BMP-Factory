namespace BPM.Application.Common;

// Generic paging envelope for list endpoints that need a total count alongside the current page
// (Phase 5.2's Process List needs total-item-count pagination; AuditLogQueryService's older
// page-only pattern predates this and is left as-is rather than retrofitted without a caller
// asking for it).
public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
