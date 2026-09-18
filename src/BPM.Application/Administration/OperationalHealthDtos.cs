namespace BPM.Application.Administration;

// Phase 9 Part 30-37 — a small, read-only Administrator diagnostic view assembled entirely from
// signals this codebase already produces (the existing ASP.NET Core health-check registration,
// NotificationDelivery rows, and non-secret appsettings values) — not a new monitoring/APM
// subsystem, no metrics database, no background collector. Every status is one of exactly three
// honest values: Healthy, Unhealthy, or NotInstrumented — never fabricated as healthy when a
// signal genuinely isn't wired up (Part 31/34: "Configured" is not the same claim as "healthy",
// and this DTO shape keeps them visibly distinct rather than collapsing them into one field).
public enum ComponentStatus
{
    Healthy,
    Unhealthy,
    NotInstrumented,
}

public record ComponentHealthDto(string Name, ComponentStatus Status, string? Detail);

// Bounded aggregate over NotificationDelivery — Part 33: "bound queries by a reasonable time
// range where appropriate." FailedCount/PendingCount/ProcessingCount are current snapshot counts
// (a Failed/Pending/Processing row has no natural "expiry," so no time window applies to those);
// SentLast24Hours is deliberately time-bounded since an unbounded "all Sent ever" count would
// grow without limit and says nothing about *current* health.
public record NotificationHealthDto(int PendingCount, int ProcessingCount, int FailedCount, int SentLast24Hours);

// Part 34 — "Configured" is presented as its own field, never conflated with "currently ticking."
// This codebase persists no worker heartbeat/last-successful-tick timestamp anywhere (confirmed
// by inspection before writing this), so a genuine runtime-health signal for the SLA scheduler
// does not exist — only its own static configuration does, and this DTO says exactly that rather
// than fabricating a Healthy/Unhealthy verdict with no evidence behind it.
public record SlaSchedulerConfigurationDto(bool Enabled, int PollIntervalSeconds, int BatchSize);

public record EmailConfigurationDto(bool Enabled, string Provider);

public record DashboardConfigurationDto(int DueSoonHours);

// Part 37 — explicitly and only the non-secret values named in this phase's own instruction.
// Never a generic "dump appsettings" view — every field here was chosen individually; adding a
// new field to this DTO should mean adding one specific, reviewed, non-secret value, never a
// broader configuration surface.
public record SafeConfigurationDto(EmailConfigurationDto Email, SlaSchedulerConfigurationDto SlaScheduler, DashboardConfigurationDto Dashboard);

public record OperationalHealthResponse(
    ComponentHealthDto Api,
    ComponentHealthDto Database,
    ComponentHealthDto ObjectStorage,
    ComponentHealthDto Cache,
    NotificationHealthDto NotificationDelivery,
    SafeConfigurationDto Configuration);

public interface IOperationalHealthService
{
    Task<OperationalHealthResponse> GetAsync(CancellationToken cancellationToken = default);
}
