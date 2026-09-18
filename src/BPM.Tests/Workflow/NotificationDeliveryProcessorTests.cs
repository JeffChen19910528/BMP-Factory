using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using BPM.EmailDelivery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace BPM.Tests.Workflow;

// Phase 6.2 — Notification Delivery. Covers NotificationDeliveryProcessor directly (the testable
// core Part Z asks to extract from the BackgroundService loop) against a real Postgres database —
// claim/process/outcome behavior for success, transient failure + retry, permanent failure,
// concurrency-safe claiming, and duplicate-send prevention.
[Collection("Postgres")]
public class NotificationDeliveryProcessorTests
{
    private static NotificationDeliveryProcessor NewProcessor(BpmDbContext db, IEmailSender sender, EmailSettings? settings = null) =>
        new(db, sender, Options.Create(settings ?? new EmailSettings { Enabled = true, MaxAttempts = 3, RetryBackoffSeconds = 0, BatchSize = 20, StaleClaimTimeoutSeconds = 300 }));

    // ProcessBatchAsync claims "up to N eligible rows across the whole table" by design (that's
    // the real worker's actual query shape) — against this suite's shared, never-truncated
    // bpm_test database, that means a batch can otherwise pick up Pending/stuck rows left behind
    // by earlier test runs, making "exactly one email sent" assertions flaky. Every test starts by
    // clearing prior Email-channel NotificationDeliveries (and any leftover blank-email test user
    // from a previous run of the one test that needs one) so each test's batch only ever contains
    // what that test itself created.
    private static async Task ResetAsync(BpmDbContext db)
    {
        await db.NotificationDeliveries.Where(d => d.Channel == DeliveryChannel.Email).ExecuteDeleteAsync();
        await db.Users.Where(u => u.Email == "").ExecuteDeleteAsync();
    }

    private static async Task<Guid> CreateUserAsync(BpmDbContext db, string? email = null)
    {
        var user = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = "Test User", Email = email ?? $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<(Guid NotificationId, Guid DeliveryId)> CreatePendingDeliveryAsync(BpmDbContext db, Guid recipientUserId, NotificationType type = NotificationType.TaskAssigned)
    {
        var notification = new Notification
        {
            RecipientUserId = recipientUserId,
            Type = type,
            Title = "Test Title",
            Message = "Test message body.",
            RelatedEntityType = "TaskInstance",
            RelatedEntityId = Guid.NewGuid().ToString(),
        };
        db.Notifications.Add(notification);
        var delivery = new NotificationDelivery { NotificationId = notification.Id, Channel = DeliveryChannel.Email, Status = DeliveryStatus.Pending };
        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync();
        return (notification.Id, delivery.Id);
    }

    private class RecordingEmailSender : IEmailSender
    {
        private readonly Func<string, bool> _shouldFail;
        public List<(string To, string Subject, string HtmlBody, string TextBody)> SentEmails { get; } = new();

        public RecordingEmailSender(Func<string, bool>? shouldFail = null) => _shouldFail = shouldFail ?? (_ => false);

        public Task SendAsync(string toAddress, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
        {
            if (_shouldFail(toAddress))
            {
                throw new InvalidOperationException("Simulated provider failure.");
            }
            SentEmails.Add((toAddress, subject, htmlBody, textBody));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ProcessBatchAsync_Disabled_DoesNothing()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        await ResetAsync(setupDb);
        var userId = await CreateUserAsync(setupDb);
        var (_, deliveryId) = await CreatePendingDeliveryAsync(setupDb, userId);

        var sender = new RecordingEmailSender();
        await using var db = PostgresFixture.CreateContext();
        var processed = await NewProcessor(db, sender, new EmailSettings { Enabled = false }).ProcessBatchAsync();

        Assert.Equal(0, processed);
        Assert.Empty(sender.SentEmails);

        await using var verifyDb = PostgresFixture.CreateContext();
        var delivery = await verifyDb.NotificationDeliveries.SingleAsync(d => d.Id == deliveryId);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
    }

    [Fact]
    public async Task ProcessBatchAsync_Success_MarksSent_AndSendsExactlyOneEmail()
    {
        var email = $"{Guid.NewGuid():N}@bpm-tests.local";
        await using var setupDb = PostgresFixture.CreateContext();
        await ResetAsync(setupDb);
        var userId = await CreateUserAsync(setupDb, email);
        var (_, deliveryId) = await CreatePendingDeliveryAsync(setupDb, userId, NotificationType.ApprovalRequired);

        var sender = new RecordingEmailSender();
        await using var db = PostgresFixture.CreateContext();
        var processed = await NewProcessor(db, sender).ProcessBatchAsync();

        Assert.Equal(1, processed);
        Assert.Single(sender.SentEmails);
        Assert.Equal(email, sender.SentEmails[0].To);
        Assert.Equal("BPM Approval Required", sender.SentEmails[0].Subject);

        await using var verifyDb = PostgresFixture.CreateContext();
        var delivery = await verifyDb.NotificationDeliveries.SingleAsync(d => d.Id == deliveryId);
        Assert.Equal(DeliveryStatus.Sent, delivery.Status);
        Assert.NotNull(delivery.SentAt);
        Assert.Equal(email, delivery.RecipientAddress);
        Assert.Equal(1, delivery.AttemptCount);
    }

    [Fact]
    public async Task ProcessBatchAsync_TransientFailure_RecordsErrorAndSchedulesRetry()
    {
        var email = $"{Guid.NewGuid():N}@bpm-tests.local";
        await using var setupDb = PostgresFixture.CreateContext();
        await ResetAsync(setupDb);
        var userId = await CreateUserAsync(setupDb, email);
        var (_, deliveryId) = await CreatePendingDeliveryAsync(setupDb, userId);

        var sender = new RecordingEmailSender(shouldFail: _ => true);
        await using var db = PostgresFixture.CreateContext();
        await NewProcessor(db, sender, new EmailSettings { Enabled = true, MaxAttempts = 3, RetryBackoffSeconds = 30 }).ProcessBatchAsync();

        await using var verifyDb = PostgresFixture.CreateContext();
        var delivery = await verifyDb.NotificationDeliveries.SingleAsync(d => d.Id == deliveryId);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.NotNull(delivery.LastError);
        Assert.NotNull(delivery.NextAttemptAt);
        Assert.True(delivery.NextAttemptAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task ProcessBatchAsync_RetryEligible_SecondAttemptSucceeds_FinalStatusSent()
    {
        var email = $"{Guid.NewGuid():N}@bpm-tests.local";
        await using var setupDb = PostgresFixture.CreateContext();
        await ResetAsync(setupDb);
        var userId = await CreateUserAsync(setupDb, email);
        var (_, deliveryId) = await CreatePendingDeliveryAsync(setupDb, userId);

        // First attempt fails; RetryBackoffSeconds=0 makes the retry immediately eligible.
        var failingSender = new RecordingEmailSender(shouldFail: _ => true);
        await using var db1 = PostgresFixture.CreateContext();
        await NewProcessor(db1, failingSender, new EmailSettings { Enabled = true, MaxAttempts = 3, RetryBackoffSeconds = 0 }).ProcessBatchAsync();

        var succeedingSender = new RecordingEmailSender();
        await using var db2 = PostgresFixture.CreateContext();
        var processed = await NewProcessor(db2, succeedingSender, new EmailSettings { Enabled = true, MaxAttempts = 3, RetryBackoffSeconds = 0 }).ProcessBatchAsync();

        Assert.Equal(1, processed);
        Assert.Single(succeedingSender.SentEmails);

        await using var verifyDb = PostgresFixture.CreateContext();
        var delivery = await verifyDb.NotificationDeliveries.SingleAsync(d => d.Id == deliveryId);
        Assert.Equal(DeliveryStatus.Sent, delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
    }

    [Fact]
    public async Task ProcessBatchAsync_ExceedsMaxAttempts_FinalStatusFailed_StopsRetrying()
    {
        var email = $"{Guid.NewGuid():N}@bpm-tests.local";
        await using var setupDb = PostgresFixture.CreateContext();
        await ResetAsync(setupDb);
        var userId = await CreateUserAsync(setupDb, email);
        var (_, deliveryId) = await CreatePendingDeliveryAsync(setupDb, userId);

        var sender = new RecordingEmailSender(shouldFail: _ => true);
        var settings = new EmailSettings { Enabled = true, MaxAttempts = 2, RetryBackoffSeconds = 0 };

        await using (var db1 = PostgresFixture.CreateContext())
        {
            await NewProcessor(db1, sender, settings).ProcessBatchAsync();
        }
        await using (var db2 = PostgresFixture.CreateContext())
        {
            await NewProcessor(db2, sender, settings).ProcessBatchAsync();
        }

        await using var verifyDb = PostgresFixture.CreateContext();
        var delivery = await verifyDb.NotificationDeliveries.SingleAsync(d => d.Id == deliveryId);
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Null(delivery.NextAttemptAt);

        // A third pass must not attempt again — Failed is not eligible.
        await using var db3 = PostgresFixture.CreateContext();
        var thirdPass = await NewProcessor(db3, sender, settings).ProcessBatchAsync();
        Assert.Equal(0, thirdPass);
    }

    [Fact]
    public async Task ProcessBatchAsync_RecipientHasNoEmail_MarksFailedWithoutCrashing()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        await ResetAsync(setupDb);
        var user = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = "No Email User", Email = "", IsActive = true };
        setupDb.Users.Add(user);
        await setupDb.SaveChangesAsync();
        var (_, deliveryId) = await CreatePendingDeliveryAsync(setupDb, user.Id);

        var sender = new RecordingEmailSender();
        await using var db = PostgresFixture.CreateContext();
        var processed = await NewProcessor(db, sender).ProcessBatchAsync();

        Assert.Equal(1, processed);
        Assert.Empty(sender.SentEmails);

        await using var verifyDb = PostgresFixture.CreateContext();
        var delivery = await verifyDb.NotificationDeliveries.SingleAsync(d => d.Id == deliveryId);
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.NotNull(delivery.LastError);
    }

    [Fact]
    public async Task ProcessBatchAsync_EmailFailure_NotificationItselfRemainsUnaffected()
    {
        var email = $"{Guid.NewGuid():N}@bpm-tests.local";
        await using var setupDb = PostgresFixture.CreateContext();
        await ResetAsync(setupDb);
        var userId = await CreateUserAsync(setupDb, email);
        var (notificationId, _) = await CreatePendingDeliveryAsync(setupDb, userId);

        var sender = new RecordingEmailSender(shouldFail: _ => true);
        await using var db = PostgresFixture.CreateContext();
        await NewProcessor(db, sender).ProcessBatchAsync();

        // The business notification is untouched by delivery failure — still exists, still
        // visible in the In-App Notification Center regardless of email outcome (Part Q).
        await using var verifyDb = PostgresFixture.CreateContext();
        var notification = await verifyDb.Notifications.SingleAsync(n => n.Id == notificationId);
        Assert.Equal("Test Title", notification.Title);
    }

    [Fact]
    public async Task ProcessBatchAsync_AlreadySent_IsNotReclaimed_NoDuplicateSend()
    {
        var email = $"{Guid.NewGuid():N}@bpm-tests.local";
        await using var setupDb = PostgresFixture.CreateContext();
        await ResetAsync(setupDb);
        var userId = await CreateUserAsync(setupDb, email);
        var (_, deliveryId) = await CreatePendingDeliveryAsync(setupDb, userId);

        var sender = new RecordingEmailSender();
        await using var db1 = PostgresFixture.CreateContext();
        await NewProcessor(db1, sender).ProcessBatchAsync();
        Assert.Single(sender.SentEmails);

        // A second pass (e.g. the next worker tick) must not resend — Sent is not eligible.
        await using var db2 = PostgresFixture.CreateContext();
        var secondPass = await NewProcessor(db2, sender).ProcessBatchAsync();

        Assert.Equal(0, secondPass);
        Assert.Single(sender.SentEmails);
    }

    [Fact]
    public async Task ProcessBatchAsync_ConcurrentClaims_ExactlyOneWorkerSendsEachDelivery()
    {
        var email = $"{Guid.NewGuid():N}@bpm-tests.local";
        await using var setupDb = PostgresFixture.CreateContext();
        await ResetAsync(setupDb);
        var userId = await CreateUserAsync(setupDb, email);
        await CreatePendingDeliveryAsync(setupDb, userId);

        var senderA = new RecordingEmailSender();
        var senderB = new RecordingEmailSender();

        await using var dbA = PostgresFixture.CreateContext();
        await using var dbB = PostgresFixture.CreateContext();

        // Two independent processors (simulating two worker instances) racing on the same batch.
        var tasks = new[]
        {
            NewProcessor(dbA, senderA).ProcessBatchAsync(),
            NewProcessor(dbB, senderB).ProcessBatchAsync(),
        };
        await Task.WhenAll(tasks);

        var totalSent = senderA.SentEmails.Count + senderB.SentEmails.Count;
        Assert.Equal(1, totalSent);
    }
}
