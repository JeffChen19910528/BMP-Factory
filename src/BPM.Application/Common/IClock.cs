namespace BPM.Application.Common;

// Phase 6.4 — SLA Scheduler. The only clock abstraction in this codebase; introduced specifically
// because SlaProcessor's evaluation of "has WarningAt/DueAt/EscalationAt passed" must be
// deterministically testable (Part C: no Thread.Sleep, no tests that depend on wall-clock timing).
// Everywhere else in this codebase that stamps a timestamp (AuditLog, Notification, SlaEngine's
// own TaskSla creation/completion) reads DateTime.UtcNow directly and deliberately stays that way
// — those are one-shot, inline stamps during a real request, not a value repeatedly compared
// against a moving threshold across polling ticks, so they gain nothing from indirection and
// Phase 6.4 does not rewrite them (see SlaEngine's own comment).
public interface IClock
{
    DateTime UtcNow { get; }
}
