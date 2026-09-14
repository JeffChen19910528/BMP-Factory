using System.Text.Json;
using BPM.Application.Common;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;

namespace BPM.Infrastructure.Services;

// Convenience audit writer for standalone actions (login, admin CRUD) that commit immediately.
// Workflow-engine operations that must stay in the same transaction as a task/process state
// change (Skill.md §32) should add an AuditLog directly to their own DbContext unit of work
// instead of calling this service, so the audit write commits or rolls back atomically with it.
public class AuditService : IAuditService
{
    private readonly BpmDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public AuditService(BpmDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task LogAsync(
        string action,
        string entityType,
        string? entityId,
        object? oldValue = null,
        object? newValue = null,
        CancellationToken cancellationToken = default)
    {
        var log = new AuditLog
        {
            TenantId = _currentUser.TenantId,
            UserId = _currentUser.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValue = oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            NewValue = newValue is null ? null : JsonSerializer.Serialize(newValue),
            IpAddress = _currentUser.IpAddress,
            UserAgent = _currentUser.UserAgent,
        };

        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync(cancellationToken);
    }
}
