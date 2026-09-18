using BPM.Application.Administration;
using BPM.Application.Forms;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using BPM.Tests.Workflow;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace BPM.Tests.Administration;

// Phase 9 — Operational Health. Verifies the response shape and, critically, that no secret
// value is ever present anywhere in it (Part 32/50/64) — real PostgreSQL for the database check
// (via a real HealthCheckService, same registration shape DependencyInjection.cs uses), a fake
// IAttachmentStorage for the object-storage signal (no real MinIO round trip needed to prove the
// service reports what the interface returns), and an in-memory IConfiguration carrying both the
// safe values this service reads AND deliberately-planted secret-shaped values it must never read.
[Collection("Postgres")]
public class OperationalHealthServiceTests
{
    private static HealthCheckService NewHealthCheckService(bool useValidConnection = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var connectionString = useValidConnection ? PostgresFixture.TestConnectionString : "Host=localhost;Port=1;Database=nonexistent;Username=x;Password=x;Timeout=1";
        services.AddHealthChecks().AddNpgSql(connectionString, name: "postgres");
        return services.BuildServiceProvider().GetRequiredService<HealthCheckService>();
    }

    private static IConfiguration NewConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:Enabled"] = "true",
            ["Email:Provider"] = "Fake",
            ["Email:Password"] = "super-secret-smtp-password",
            ["SlaScheduler:Enabled"] = "true",
            ["SlaScheduler:PollIntervalSeconds"] = "60",
            ["SlaScheduler:BatchSize"] = "50",
            ["Dashboard:DueSoonHours"] = "24",
            ["Jwt:Secret"] = "super-secret-jwt-signing-key",
            ["Minio:SecretKey"] = "super-secret-minio-key",
            ["ConnectionStrings:Default"] = "Host=localhost;Password=super-secret-db-password",
        }).Build();

    private static OperationalHealthService NewService(BpmDbContext db, bool objectStorageHealthy = true, bool databaseHealthy = true) =>
        new(db, NewHealthCheckService(databaseHealthy), new FakeAttachmentStorage(objectStorageHealthy), NewConfiguration());

    [Fact]
    public async Task GetAsync_HealthyDatabaseAndStorage_ReportsHealthy()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).GetAsync();

        Assert.Equal(ComponentStatus.Healthy, result.Api.Status);
        Assert.Equal(ComponentStatus.Healthy, result.Database.Status);
        Assert.Equal(ComponentStatus.Healthy, result.ObjectStorage.Status);
    }

    [Fact]
    public async Task GetAsync_ObjectStorageUnavailable_ReportsUnhealthy_NeverFakedHealthy()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db, objectStorageHealthy: false).GetAsync();

        Assert.Equal(ComponentStatus.Unhealthy, result.ObjectStorage.Status);
    }

    [Fact]
    public async Task GetAsync_Cache_IsReportedNotInstrumented_NeverFakedHealthy()
    {
        // Part 31/34 — Redis has no wired client anywhere in this codebase; the response must say
        // so honestly rather than claim Healthy with nothing behind that claim.
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).GetAsync();

        Assert.Equal(ComponentStatus.NotInstrumented, result.Cache.Status);
    }

    [Fact]
    public async Task GetAsync_SafeConfiguration_ReportsOnlyNonSecretValues()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).GetAsync();

        Assert.True(result.Configuration.Email.Enabled);
        Assert.Equal("Fake", result.Configuration.Email.Provider);
        Assert.True(result.Configuration.SlaScheduler.Enabled);
        Assert.Equal(60, result.Configuration.SlaScheduler.PollIntervalSeconds);
        Assert.Equal(50, result.Configuration.SlaScheduler.BatchSize);
        Assert.Equal(24, result.Configuration.Dashboard.DueSoonHours);
    }

    [Fact]
    public async Task GetAsync_ResponseNeverContainsAnySecretValue()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).GetAsync();

        var serialized = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.DoesNotContain("super-secret-smtp-password", serialized);
        Assert.DoesNotContain("super-secret-jwt-signing-key", serialized);
        Assert.DoesNotContain("super-secret-minio-key", serialized);
        Assert.DoesNotContain("super-secret-db-password", serialized);
    }

    [Fact]
    public async Task GetAsync_NotificationDelivery_ReportsRealAggregateCounts()
    {
        await using var db = PostgresFixture.CreateContext();
        var notification = new Notification
        {
            RecipientUserId = Guid.NewGuid(),
            Type = NotificationType.TaskAssigned,
            Title = "Test",
            Message = "Test",
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync();

        var pendingDelivery = new NotificationDelivery { NotificationId = notification.Id, Channel = DeliveryChannel.Email, Status = DeliveryStatus.Pending };
        var failedDelivery = new NotificationDelivery { NotificationId = notification.Id, Channel = DeliveryChannel.Email, Status = DeliveryStatus.Failed };
        db.NotificationDeliveries.AddRange(pendingDelivery, failedDelivery);
        await db.SaveChangesAsync();

        var result = await NewService(db).GetAsync();

        Assert.True(result.NotificationDelivery.PendingCount >= 1);
        Assert.True(result.NotificationDelivery.FailedCount >= 1);
    }

    private class FakeAttachmentStorage : IAttachmentStorage
    {
        private readonly bool _healthy;
        public FakeAttachmentStorage(bool healthy) => _healthy = healthy;
        public Task SaveAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream());
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default) => Task.FromResult(_healthy);
    }
}
