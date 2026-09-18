using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BPM.Workflow.Engine;

// Phase 6.4 — a thin scheduler around SlaProcessor, mirroring EmailDeliveryWorker
// (BPM.EmailDelivery) exactly: each tick opens its own DI scope (BpmDbContext is scoped, this is a
// singleton-lifetime BackgroundService), never blocks HTTP requests, and never crashes on a single
// bad tick. Part B: this is a wholly independent background concern from EmailDeliveryWorker — it
// never references IEmailSender/NotificationDelivery, and EmailDeliveryWorker was not modified to
// know anything about SLA.
public class SlaSchedulerWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SlaSchedulerSettings _settings;
    private readonly ILogger<SlaSchedulerWorker> _logger;

    public SlaSchedulerWorker(IServiceScopeFactory scopeFactory, IOptions<SlaSchedulerSettings> settings, ILogger<SlaSchedulerWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Starts unconditionally regardless of SlaSchedulerSettings.Enabled (Part AM: "If SLA
        // scheduler is disabled: Application must still start normally") — SlaProcessor itself is
        // what no-ops when disabled, exactly like EmailDeliveryWorker/NotificationDeliveryProcessor.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<SlaProcessor>();
                var changedCount = await processor.ProcessBatchAsync(stoppingToken);
                if (changedCount > 0)
                {
                    _logger.LogInformation("SlaSchedulerWorker processed {Count} SLA transition(s)/notification(s).", changedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single bad tick (e.g. a transient DB hiccup) must never crash the worker —
                // every claim is transactional (SlaProcessor), so nothing is left half-applied; the
                // next poll simply re-evaluates the same candidates.
                _logger.LogError(ex, "SlaSchedulerWorker tick failed; will retry on the next poll.");
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
