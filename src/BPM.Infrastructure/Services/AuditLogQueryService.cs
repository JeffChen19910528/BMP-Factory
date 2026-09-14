using BPM.Application.Audit;
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

    public async Task<IReadOnlyList<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
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

        return await q
            .OrderByDescending(a => a.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditLogDto(a.Id, a.UserId, a.Action, a.EntityType, a.EntityId, a.OldValue, a.NewValue, a.Timestamp))
            .ToListAsync(cancellationToken);
    }
}
