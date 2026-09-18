namespace BPM.EmailDelivery;

// Phase 6.2. Mirrors BPM.Infrastructure.MinioSettings' own shape/placement exactly (a plain
// options class, bound from an appsettings.json/environment-variable section of the same name,
// never hard-coded) — see DependencyInjection.cs's AddNotificationDelivery for the binding.
public class EmailSettings
{
    public const string SectionName = "Email";

    // Master switch (Part AB: "Do not make SMTP availability a prerequisite for application
    // startup" / "If Email is disabled: the BPM system must continue operating normally"). When
    // false, EmailDeliveryWorker still runs (so it stays a harmless no-op loop, not a startup
    // dependency) but never claims a delivery — every Email NotificationDelivery row simply stays
    // Pending forever, and In-App notifications are entirely unaffected either way.
    public bool Enabled { get; set; }

    // "Smtp" (production) or "Fake" (deterministic, in-process — local dev and this repo's own
    // Docker Compose stack, which has no real mail server, use this; see FakeEmailSender's own
    // comment for why a Mailhog-style container was deliberately not added for this phase). Never
    // let "Fake" reach an actual production deployment — this is a plain config value, not a
    // build-time flag, so it's on whoever deploys to set Provider=Smtp with real credentials.
    public string Provider { get; set; } = "Fake";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = true;
    public string FromAddress { get; set; } = "bpm-platform@example.com";
    public string FromName { get; set; } = "BPM Platform";

    public int PollIntervalSeconds { get; set; } = 15;
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 5;

    // Base delay for exponential-ish backoff: attempt N's retry is scheduled
    // RetryBackoffSeconds * N after that attempt fails (see NotificationDeliveryProcessor). Kept
    // as one base value rather than a per-attempt array — simplest representation that still
    // produces an increasing delay, per Part P's own "do not create an elaborate... system."
    public int RetryBackoffSeconds { get; set; } = 30;

    // How long a Processing row is allowed to sit claimed before it's considered stale (worker
    // crashed mid-attempt) and eligible to be reclaimed — Part AG's "Processing timeout / lease
    // expiration" recovery strategy.
    public int StaleClaimTimeoutSeconds { get; set; } = 300;
}
