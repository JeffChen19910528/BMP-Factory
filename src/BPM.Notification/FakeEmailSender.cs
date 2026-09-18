using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace BPM.EmailDelivery;

// Phase 6.2 — the "Fake" IEmailSender (EmailSettings.Provider = "Fake"), selected by
// DependencyInjection.cs's AddNotificationDelivery when no real mail server is configured. Used
// by local dev and this repo's own docker-compose stack (Part J: no Mailhog-style container was
// added just for this phase — that would be exactly the "large infrastructure dependency merely
// for this phase" Part AA warns against). Clearly separate from SmtpEmailSender (Part J) — the
// two are selected by one config string, never both, and this one is never what a real deployment
// should point at (EmailSettings.Provider's own doc comment covers that).
//
// Deterministic, not random: a recipient address whose local-part starts with FailureTriggerPrefix
// simulates a provider failure, used by the live retry/max-attempt tests (see
// notification.delivery.live.test.ts's Scenarios D/E/F) to exercise those paths over real HTTP
// against a real running container — impossible to do by swapping DI mid-test-suite across a
// process boundary, and this avoids sending real external email or standing up a Mailhog-style
// container just for this phase. An optional trailing number controls how many attempts fail
// before succeeding: "faildelivery@..." (no number) always fails; "faildelivery1-x@..." fails
// exactly once then succeeds (drives the "retry eventually succeeds" scenario);
// "faildelivery99-x@..." fails far more times than any realistic MaxAttempts (drives the
// "exhausts retries, ends Failed" scenario). Registered as a singleton (see DependencyInjection.cs)
// specifically so this per-address failure count survives across the worker's separate DI scopes
// per poll tick — a Scoped registration would reset to zero every tick and could never fail "the
// first N times."
public class FakeEmailSender : IEmailSender
{
    public const string FailureTriggerPrefix = "faildelivery";
    private static readonly Regex TriggerPattern = new($"^{FailureTriggerPrefix}(\\d*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, int> _attemptsSoFar = new();

    public Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
    {
        var match = TriggerPattern.Match(toAddress);
        if (match.Success)
        {
            var failCount = match.Groups[1].Value.Length > 0 ? int.Parse(match.Groups[1].Value) : int.MaxValue;
            var attemptNumber = _attemptsSoFar.AddOrUpdate(toAddress, 1, (_, count) => count + 1);
            if (attemptNumber <= failCount)
            {
                throw new InvalidOperationException("Simulated provider failure (FakeEmailSender test trigger).");
            }
        }

        return Task.CompletedTask;
    }
}
