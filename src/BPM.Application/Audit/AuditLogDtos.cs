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
    Task<IReadOnlyList<AuditLogDto>> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default);
}
