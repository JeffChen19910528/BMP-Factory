using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BPM.EmailDelivery;

// Phase 6.2 — a thin scheduler around NotificationDeliveryProcessor (Part Z: extract the testable
// unit, keep the HostedService itself dumb). Each tick opens its own DI scope (BpmDbContext is
// scoped, this is a singleton-lifetime BackgroundService) so nothing here holds a DbContext across
// polls. Never blocks HTTP requests — this runs entirely on its own loop, independent of
// Program.cs's request pipeline.
public class EmailDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EmailSettings _settings;
    private readonly ILogger<EmailDeliveryWorker> _logger;

    public EmailDeliveryWorker(IServiceScopeFactory scopeFactory, IOptions<EmailSettings> settings, ILogger<EmailDeliveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Starts unconditionally regardless of EmailSettings.Enabled (Part AB: "worker starts
        // safely when Email is disabled... does not prevent API startup") — NotificationDeliveryProcessor
        // itself is what actually no-ops when disabled, so this loop just keeps ticking harmlessly.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<NotificationDeliveryProcessor>();
                var processedCount = await processor.ProcessBatchAsync(stoppingToken);
                if (processedCount > 0)
                {
                    _logger.LogInformation("EmailDeliveryWorker processed {Count} pending delivery(ies).", processedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single bad tick (e.g. a transient DB hiccup) must never crash the worker —
                // there is always a next poll.
                _logger.LogError(ex, "EmailDeliveryWorker tick failed; will retry on the next poll.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _settings.PollIntervalSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
