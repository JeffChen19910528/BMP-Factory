using BPM.Application.Audit;
using BPM.Application.Common;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BPM.Infrastructure.Services;

public class AuditLogQueryService : IAuditLogQueryService
{
    private readonly BpmDbContext _db;

    public AuditLogQueryService(BpmDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var q = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (query.UserId is not null)
        {
            q = q.Where(a => a.UserId == query.UserId);
        }
        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            q = q.Where(a => a.EntityType == query.EntityType);
        }
        if (!string.IsNullOrWhiteSpace(query.EntityId))
        {
            q = q.Where(a => a.EntityId == query.EntityId);
        }
        if (query.From is not null)
        {
            q = q.Where(a => a.Timestamp >= query.From);
        }
        if (query.To is not null)
        {
            q = q.Where(a => a.Timestamp <= query.To);
        }

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 200 ? 50 : query.PageSize;

        // Phase 12 — same overflow guard Phase 7.2.3 already established in
        // ProcessMonitoringQueryService: plain `int` arithmetic on (page - 1) * pageSize can
        // overflow at an extreme Page value and wrap to a negative Skip() argument, which
        // PostgreSQL rejects with a raw, unmapped exception. Computing in `long` and clamping to
        // int.MaxValue keeps normal pagination completely unchanged — a page that far out simply
        // yields zero rows, the correct behavior for an out-of-range page.
        var skip = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

        var totalCount = await q.CountAsync(cancellationToken);
        var items = await q
            .OrderByDescending(a => a.Timestamp)
            .Skip(skip)
            .Take(pageSize)
            .Select(a => new AuditLogDto(a.Id, a.UserId, a.Action, a.EntityType, a.EntityId, a.OldValue, a.NewValue, a.Timestamp))
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogDto>(items, totalCount, page, pageSize);
    }
}
