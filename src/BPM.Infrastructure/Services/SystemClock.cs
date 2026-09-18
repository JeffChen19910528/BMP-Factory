using BPM.Application.Common;

namespace BPM.Infrastructure.Services;

// Phase 6.4 — the production IClock implementation. Registered Singleton (stateless, exactly like
// AssignmentResolver's own statelessness) — SlaSchedulerWorker resolves it per DI scope, but the
// value itself is always just DateTime.UtcNow, so there's nothing to keep scoped.
public class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
