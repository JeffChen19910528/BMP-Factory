using BPM.Application.Administration;
using BPM.Application.Forms;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BPM.Infrastructure.Services;

// Phase 9 — Operational Health. Reuses existing infrastructure only: the health-check
// registration DependencyInjection.cs already sets up (`AddHealthChecks().AddNpgSql(...)`),
// IAttachmentStorage's new IsHealthyAsync probe, NotificationDelivery's own existing rows, and a
// handful of individually-named, non-secret IConfiguration values. No new package, no new
// project reference (IConfiguration is read directly here rather than adding a dependency on
// BPM.Notification/BPM.Workflow's own strongly-typed EmailSettings/SlaSchedulerSettings classes,
// which BPM.Infrastructure does not and should not reference — see this phase's own Discovery
// note on layering).
public class OperationalHealthService : IOperationalHealthService
{
    private static readonly TimeSpan SentLookback = TimeSpan.FromHours(24);

    private readonly BpmDbContext _db;
    private readonly HealthCheckService _healthCheckService;
    private readonly IAttachmentStorage _attachmentStorage;
    private readonly IConfiguration _configuration;

    public OperationalHealthService(BpmDbContext db, HealthCheckService healthCheckService, IAttachmentStorage attachmentStorage, IConfiguration configuration)
    {
        _db = db;
        _healthCheckService = healthCheckService;
        _attachmentStorage = attachmentStorage;
        _configuration = configuration;
    }

    public async Task<OperationalHealthResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        // Api: if this method is executing at all, the API process itself is up — no separate
        // probe needed for this one.
        var api = new ComponentHealthDto("API", ComponentStatus.Healthy, null);

        var database = await GetDatabaseHealthAsync(cancellationToken);
        var objectStorage = await GetObjectStorageHealthAsync(cancellationToken);

        // Cache (Redis): confirmed by inspection — no IConnectionMultiplexer/StackExchange.Redis
        // reference exists anywhere in this codebase today. Redis is provisioned in
        // docker-compose.yml but never actually wired into the application, so there is honestly
        // nothing to probe — reported as NotInstrumented, never fabricated as Healthy.
        var cache = new ComponentHealthDto("Cache (Redis)", ComponentStatus.NotInstrumented, "No Redis client is wired into the application yet.");

        var notificationHealth = await GetNotificationHealthAsync(cancellationToken);
        var configuration = GetSafeConfiguration();

        return new OperationalHealthResponse(api, database, objectStorage, cache, notificationHealth, configuration);
    }

    private async Task<ComponentHealthDto> GetDatabaseHealthAsync(CancellationToken cancellationToken)
    {
        var report = await _healthCheckService.CheckHealthAsync(r => r.Name == "postgres", cancellationToken);
        var status = report.Status == HealthStatus.Healthy ? ComponentStatus.Healthy : ComponentStatus.Unhealthy;
        var detail = report.Entries.TryGetValue("postgres", out var entry) ? entry.Description : null;
        return new ComponentHealthDto("PostgreSQL", status, detail);
    }

    private async Task<ComponentHealthDto> GetObjectStorageHealthAsync(CancellationToken cancellationToken)
    {
        var healthy = await _attachmentStorage.IsHealthyAsync(cancellationToken);
        return new ComponentHealthDto("Object Storage (MinIO)", healthy ? ComponentStatus.Healthy : ComponentStatus.Unhealthy, null);
    }

    private async Task<NotificationHealthDto> GetNotificationHealthAsync(CancellationToken cancellationToken)
    {
        var counts = await _db.NotificationDeliveries
            .AsNoTracking()
            .GroupBy(d => d.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int CountOf(DeliveryStatus status) => counts.FirstOrDefault(x => x.Status == status)?.Count ?? 0;

        var since = DateTime.UtcNow - SentLookback;
        var sentRecently = await _db.NotificationDeliveries
            .AsNoTracking()
            .CountAsync(d => d.Status == DeliveryStatus.Sent && d.SentAt != null && d.SentAt >= since, cancellationToken);

        return new NotificationHealthDto(CountOf(DeliveryStatus.Pending), CountOf(DeliveryStatus.Processing), CountOf(DeliveryStatus.Failed), sentRecently);
    }

    // Part 37 — exactly these five values, individually named, all non-secret. Never a generic
    // configuration dump; SMTP/DB/MinIO credentials, the JWT secret, and connection strings are
    // never read here at all, let alone exposed.
    private SafeConfigurationDto GetSafeConfiguration()
    {
        var email = new EmailConfigurationDto(
            _configuration.GetValue("Email:Enabled", false),
            _configuration.GetValue("Email:Provider", string.Empty) ?? string.Empty);

        var slaScheduler = new SlaSchedulerConfigurationDto(
            _configuration.GetValue("SlaScheduler:Enabled", false),
            _configuration.GetValue("SlaScheduler:PollIntervalSeconds", 0),
            _configuration.GetValue("SlaScheduler:BatchSize", 0));

        var dashboard = new DashboardConfigurationDto(_configuration.GetValue("Dashboard:DueSoonHours", 0));

        return new SafeConfigurationDto(email, slaScheduler, dashboard);
    }
}
