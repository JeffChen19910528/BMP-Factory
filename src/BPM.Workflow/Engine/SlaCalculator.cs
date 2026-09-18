using BPM.Application.Common;

namespace BPM.Workflow.Engine;

public readonly record struct SlaCalculationResult(DateTime StartedAt, DateTime WarningAt, DateTime DueAt);

// Phase 6.3 — a pure, deterministic calculator (Part AA: no DateTime.Now, no Thread.Sleep,
// startedAtUtc always passed in explicitly) so it's directly unit-testable without a database or
// real-time waiting — the same "pure/DB-free, public static" shape as FormFormulaEvaluator/
// WorkflowDefinitionValidator elsewhere in this project. A simple elapsed-time model only
// (Part C): no business calendars, holidays, working hours, or timezone handling — Duration and
// WarningOffset are both plain minute counts added directly to startedAtUtc.
//
// Validation lives here too (not just in the request validator) because this is the one place
// that can never be bypassed — SlaEngine calls this for every applicable task, using the
// policy's already-validated values, but a defensive re-check here means a corrupted/legacy
// policy row can never silently produce a nonsensical WarningAt >= DueAt for a real running task.
public static class SlaCalculator
{
    public static SlaCalculationResult Calculate(int durationMinutes, int warningOffsetMinutes, DateTime startedAtUtc)
    {
        if (durationMinutes <= 0)
        {
            throw new BadRequestAppException("INVALID_SLA_DURATION", "SLA duration must be a positive number of minutes.");
        }
        if (warningOffsetMinutes < 0)
        {
            throw new BadRequestAppException("INVALID_SLA_WARNING_OFFSET", "SLA warning offset must not be negative.");
        }
        if (warningOffsetMinutes >= durationMinutes)
        {
            throw new BadRequestAppException("INVALID_SLA_WARNING_OFFSET", "SLA warning offset must be less than the duration.");
        }

        var dueAt = startedAtUtc.AddMinutes(durationMinutes);
        var warningAt = dueAt.AddMinutes(-warningOffsetMinutes);
        return new SlaCalculationResult(startedAtUtc, warningAt, dueAt);
    }
}
