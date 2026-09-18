using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BPM.EmailDelivery;

// Phase 6.2 — the testable core the spec (Part Z) asks to extract separately from the
// BackgroundService loop: EmailDeliveryWorker is a thin scheduler, this class does the actual
// claim -> resolve -> render -> send -> record-outcome work, deterministically and without any
// Task.Delay/sleep of its own — a test can call ProcessBatchAsync directly and assert on the
// resulting database state.
public class NotificationDeliveryProcessor
{
    private readonly BpmDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly EmailSettings _settings;

    public NotificationDeliveryProcessor(BpmDbContext db, IEmailSender emailSender, IOptions<EmailSettings> settings)
    {
        _db = db;
        _emailSender = emailSender;
        _settings = settings.Value;
    }

    // Returns how many deliveries were claimed and attempted this call (0 when Email is disabled,
    // or when nothing is currently eligible) — purely for worker logging/test assertions, not a
    // control-flow signal.
    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
        {
            return 0;
        }

        var claimed = await ClaimBatchAsync(cancellationToken);
        foreach (var delivery in claimed)
        {
            await ProcessOneAsync(delivery, cancellationToken);
        }
        return claimed.Count;
    }

    // Part O: concurrency-safe claiming without RowVersion — a single conditional UPDATE
    // (`ExecuteUpdateAsync`) per candidate row, re-checking the exact same eligibility predicate
    // used to find it as part of the UPDATE's own WHERE clause. Two workers racing on the same row
    // both issue this UPDATE; PostgreSQL serializes them at the row level, so at most one affects
    // a row (rows == 1) — the other's WHERE no longer matches (the row is already Processing with
    // a fresh LastAttemptAt) and affects zero rows. No optimistic-concurrency retry loop needed.
    private async Task<List<NotificationDelivery>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var staleThreshold = now.AddSeconds(-_settings.StaleClaimTimeoutSeconds);
        var eligible = EligiblePredicate(now, staleThreshold);

        var candidateIds = await _db.NotificationDeliveries
            .Where(d => d.Channel == DeliveryChannel.Email)
            .Where(eligible)
            .OrderBy(d => d.NextAttemptAt)
            .Take(_settings.BatchSize)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        var claimed = new List<NotificationDelivery>();
        foreach (var id in candidateIds)
        {
            var affected = await _db.NotificationDeliveries
                .Where(d => d.Id == id)
                .Where(eligible)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.Status, DeliveryStatus.Processing)
                    .SetProperty(d => d.LastAttemptAt, now)
                    .SetProperty(d => d.AttemptCount, d => d.AttemptCount + 1)
                    .SetProperty(d => d.UpdatedAt, now), cancellationToken);

            if (affected == 1)
            {
                var delivery = await _db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.Id == id, cancellationToken);
                claimed.Add(delivery);
            }
        }

        return claimed;
    }

    // An Expression (not a plain method) so EF Core can translate it into SQL when spliced into a
    // .Where(...) — a regular C# method call in a LINQ expression tree can't be translated
    // (EF has no way to "see inside" it), which is exactly the failure this replaced.
    private static System.Linq.Expressions.Expression<Func<NotificationDelivery, bool>> EligiblePredicate(DateTime now, DateTime staleThreshold) =>
        d => (d.Status == DeliveryStatus.Pending && (d.NextAttemptAt == null || d.NextAttemptAt <= now))
            || (d.Status == DeliveryStatus.Processing && d.LastAttemptAt != null && d.LastAttemptAt <= staleThreshold);

    private async Task ProcessOneAsync(NotificationDelivery claimed, CancellationToken cancellationToken)
    {
        var notification = await _db.Notifications.AsNoTracking().SingleOrDefaultAsync(n => n.Id == claimed.NotificationId, cancellationToken);
        if (notification is null)
        {
            // Should not normally happen (Notification/Delivery are created together, and neither
            // is ever deleted) — fail closed rather than retry forever against a row that will
            // never resolve.
            await MarkPermanentFailureAsync(claimed.Id, null, "The related notification no longer exists.", cancellationToken);
            return;
        }

        // Part G: recipient email comes from User.Email via Notification.RecipientUserId, resolved
        // fresh at send time — never trusted from the frontend, never denormalized at creation.
        var recipientEmail = await _db.Users.AsNoTracking()
            .Where(u => u.Id == notification.RecipientUserId)
            .Select(u => u.Email)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(recipientEmail))
        {
            // A missing email address will not self-heal on retry — permanent, not transient.
            await MarkPermanentFailureAsync(claimed.Id, null, "Recipient has no email address on file.", cancellationToken);
            return;
        }

        var (subject, htmlBody, textBody) = EmailTemplates.Render(notification.Type, notification.Title, notification.Message);

        try
        {
            await _emailSender.SendAsync(recipientEmail, subject, htmlBody, textBody, cancellationToken);
            await MarkSentAsync(claimed.Id, recipientEmail, cancellationToken);
        }
        catch (Exception ex)
        {
            await MarkTransientFailureAsync(claimed.Id, recipientEmail, claimed.AttemptCount, SanitizeError(ex.Message), cancellationToken);
        }
    }

    private async Task MarkSentAsync(Guid deliveryId, string recipientEmail, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await _db.NotificationDeliveries.Where(d => d.Id == deliveryId).ExecuteUpdateAsync(setters => setters
            .SetProperty(d => d.Status, DeliveryStatus.Sent)
            .SetProperty(d => d.SentAt, now)
            .SetProperty(d => d.NextAttemptAt, (DateTime?)null)
            .SetProperty(d => d.RecipientAddress, recipientEmail)
            .SetProperty(d => d.LastError, (string?)null)
            .SetProperty(d => d.UpdatedAt, now), cancellationToken);
    }

    // Part P: bounded retry with a simple linear backoff (RetryBackoffSeconds * attemptCount) —
    // attempt 1 fails -> retry after RetryBackoffSeconds, attempt 2 fails -> retry after 2x, and
    // so on, until attemptCount reaches MaxAttempts, at which point it's Failed permanently.
    private async Task MarkTransientFailureAsync(Guid deliveryId, string recipientEmail, int attemptCount, string error, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (attemptCount >= _settings.MaxAttempts)
        {
            await _db.NotificationDeliveries.Where(d => d.Id == deliveryId).ExecuteUpdateAsync(setters => setters
                .SetProperty(d => d.Status, DeliveryStatus.Failed)
                .SetProperty(d => d.NextAttemptAt, (DateTime?)null)
                .SetProperty(d => d.RecipientAddress, recipientEmail)
                .SetProperty(d => d.LastError, error)
                .SetProperty(d => d.UpdatedAt, now), cancellationToken);
            return;
        }

        var nextAttemptAt = now.AddSeconds(_settings.RetryBackoffSeconds * attemptCount);
        await _db.NotificationDeliveries.Where(d => d.Id == deliveryId).ExecuteUpdateAsync(setters => setters
            .SetProperty(d => d.Status, DeliveryStatus.Pending)
            .SetProperty(d => d.NextAttemptAt, nextAttemptAt)
            .SetProperty(d => d.RecipientAddress, recipientEmail)
            .SetProperty(d => d.LastError, error)
            .SetProperty(d => d.UpdatedAt, now), cancellationToken);
    }

    private async Task MarkPermanentFailureAsync(Guid deliveryId, string? recipientEmail, string error, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await _db.NotificationDeliveries.Where(d => d.Id == deliveryId).ExecuteUpdateAsync(setters => setters
            .SetProperty(d => d.Status, DeliveryStatus.Failed)
            .SetProperty(d => d.NextAttemptAt, (DateTime?)null)
            .SetProperty(d => d.RecipientAddress, recipientEmail)
            .SetProperty(d => d.LastError, error)
            .SetProperty(d => d.UpdatedAt, now), cancellationToken);
    }

    // Part R: "Keep LastError bounded and sanitized" — never a raw stack trace, never long enough
    // to plausibly carry an embedded credential or response dump.
    private static string SanitizeError(string message)
    {
        const int maxLength = 500;
        var singleLine = message.Replace('\n', ' ').Replace('\r', ' ');
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength];
    }
}
