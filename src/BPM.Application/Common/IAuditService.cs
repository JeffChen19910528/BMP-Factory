namespace BPM.Application.Common;

public interface IAuditService
{
    Task LogAsync(
        string action,
        string entityType,
        string? entityId,
        object? oldValue = null,
        object? newValue = null,
        CancellationToken cancellationToken = default);
}
