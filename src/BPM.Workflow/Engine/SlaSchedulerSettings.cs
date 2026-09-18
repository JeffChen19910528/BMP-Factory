namespace BPM.Workflow.Engine;

// Phase 6.4 — mirrors EmailSettings' own shape (BPM.EmailDelivery.EmailSettings) exactly: a plain
// options class bound from configuration, development-safe defaults, no secrets. Enabled=false
// must still let the application start normally (SlaSchedulerWorker keeps polling harmlessly,
// SlaProcessor.ProcessBatchAsync no-ops immediately — same "worker always starts, processor
// decides whether to actually do anything" split Part AB/Part M require).
public class SlaSchedulerSettings
{
    public const string SectionName = "SlaScheduler";

    public bool Enabled { get; set; } = true;

    // Part M: "a reasonable initial interval may be 1 minute" — chosen as the default because SLA
    // durations are expressed in whole minutes (SlaCalculator), so sub-minute precision buys
    // nothing in production; docker-compose.yml overrides this to a much shorter interval for
    // local dev/live-test purposes only, exactly as EmailSettings.PollIntervalSeconds already does.
    public int PollIntervalSeconds { get; set; } = 60;

    public int BatchSize { get; set; } = 50;
}
