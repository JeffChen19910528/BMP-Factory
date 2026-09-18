using BPM.Application.Common;
using BPM.Application.Notifications;
using BPM.Domain.Entities;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BPM.Tests.Workflow;

// Phase 6.1 — Notification Foundation. Covers the read/query side (NotificationService) directly
// against a real Postgres database — creation-via-workflow-trigger is covered separately in
// NotificationIntegrationTests.cs (real WorkflowEngine/ApprovalEngine, real recipient resolution).
[Collection("Postgres")]
public class NotificationServiceTests
{
    private static NotificationService NewService(BpmDbContext db) => new(db);

    private static Notification NewNotification(Guid recipientUserId, NotificationType type = NotificationType.TaskAssigned, bool isRead = false) => new()
    {
        RecipientUserId = recipientUserId,
        Type = type,
        Title = "Test",
        Message = "Test message",
        RelatedEntityType = "TaskInstance",
        RelatedEntityId = Guid.NewGuid().ToString(),
        IsRead = isRead,
    };

    [Fact]
    public async Task GetForUserAsync_ReturnsOnlyCallersNotifications_NewestFirst()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var setupDb = PostgresFixture.CreateContext();
        var older = NewNotification(userA);
        older.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        var newer = NewNotification(userA);
        newer.CreatedAt = DateTime.UtcNow;
        setupDb.Notifications.AddRange(older, newer, NewNotification(userB));
        await setupDb.SaveChangesAsync();

        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).GetForUserAsync(userA, new NotificationQuery());

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Items, i => Assert.True(i.Id == older.Id || i.Id == newer.Id));
        Assert.Equal(newer.Id, result.Items[0].Id);
    }

    [Fact]
    public async Task GetForUserAsync_UnreadOnly_FiltersReadNotifications()
    {
        var userId = Guid.NewGuid();

        await using var setupDb = PostgresFixture.CreateContext();
        var read = NewNotification(userId, isRead: true);
        var unread = NewNotification(userId, isRead: false);
        setupDb.Notifications.AddRange(read, unread);
        await setupDb.SaveChangesAsync();

        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).GetForUserAsync(userId, new NotificationQuery(UnreadOnly: true));

        Assert.Single(result.Items);
        Assert.Equal(unread.Id, result.Items[0].Id);
    }

    [Fact]
    public async Task GetForUserAsync_Pagination_ReturnsCorrectTotalCountAndPage()
    {
        var userId = Guid.NewGuid();

        await using var setupDb = PostgresFixture.CreateContext();
        for (var i = 0; i < 5; i++)
        {
            setupDb.Notifications.Add(NewNotification(userId));
        }
        await setupDb.SaveChangesAsync();

        await using var db = PostgresFixture.CreateContext();
        var page1 = await NewService(db).GetForUserAsync(userId, new NotificationQuery(Page: 1, PageSize: 2));

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
    }

    [Fact]
    public async Task GetUnreadCountAsync_CountsOnlyCallersUnread()
    {
        var userId = Guid.NewGuid();

        await using var setupDb = PostgresFixture.CreateContext();
        setupDb.Notifications.AddRange(NewNotification(userId), NewNotification(userId), NewNotification(userId, isRead: true), NewNotification(Guid.NewGuid()));
        await setupDb.SaveChangesAsync();

        await using var db = PostgresFixture.CreateContext();
        var count = await NewService(db).GetUnreadCountAsync(userId);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task MarkReadAsync_SetsIsReadAndReadAt()
    {
        var userId = Guid.NewGuid();

        await using var setupDb = PostgresFixture.CreateContext();
        var notification = NewNotification(userId);
        setupDb.Notifications.Add(notification);
        await setupDb.SaveChangesAsync();

        await using var db = PostgresFixture.CreateContext();
        await NewService(db).MarkReadAsync(notification.Id, userId);

        await using var verifyDb = PostgresFixture.CreateContext();
        var reloaded = await verifyDb.Notifications.SingleAsync(n => n.Id == notification.Id);
        Assert.True(reloaded.IsRead);
        Assert.NotNull(reloaded.ReadAt);
    }

    [Fact]
    public async Task MarkReadAsync_IsIdempotent_SecondCallSucceedsWithoutChangingOriginalReadAt()
    {
        var userId = Guid.NewGuid();

        await using var setupDb = PostgresFixture.CreateContext();
        var notification = NewNotification(userId);
        setupDb.Notifications.Add(notification);
        await setupDb.SaveChangesAsync();

        await using var firstDb = PostgresFixture.CreateContext();
        await NewService(firstDb).MarkReadAsync(notification.Id, userId);

        await using var verifyDb1 = PostgresFixture.CreateContext();
        var firstReadAt = (await verifyDb1.Notifications.SingleAsync(n => n.Id == notification.Id)).ReadAt;

        // Second mark-read must succeed (not throw) and must not stamp a new ReadAt.
        await using var secondDb = PostgresFixture.CreateContext();
        await NewService(secondDb).MarkReadAsync(notification.Id, userId);

        await using var verifyDb2 = PostgresFixture.CreateContext();
        var reloaded = await verifyDb2.Notifications.SingleAsync(n => n.Id == notification.Id);
        Assert.True(reloaded.IsRead);
        Assert.Equal(firstReadAt, reloaded.ReadAt);
    }

    [Fact]
    public async Task MarkReadAsync_NonexistentNotification_ReturnsNotFound()
    {
        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<NotFoundAppException>(() => NewService(db).MarkReadAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("NOTIFICATION_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task MarkReadAsync_AnotherUsersNotification_Rejected_IDOR()
    {
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();

        await using var setupDb = PostgresFixture.CreateContext();
        var notification = NewNotification(owner);
        setupDb.Notifications.Add(notification);
        await setupDb.SaveChangesAsync();

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() => NewService(db).MarkReadAsync(notification.Id, attacker));
        Assert.Equal("NOTIFICATION_NOT_AUTHORIZED", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        var reloaded = await verifyDb.Notifications.SingleAsync(n => n.Id == notification.Id);
        Assert.False(reloaded.IsRead);
    }

    [Fact]
    public async Task MarkAllReadAsync_OnlyAffectsCallersOwnNotifications()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await using var setupDb = PostgresFixture.CreateContext();
        setupDb.Notifications.AddRange(NewNotification(userA), NewNotification(userA), NewNotification(userB));
        await setupDb.SaveChangesAsync();

        await using var db = PostgresFixture.CreateContext();
        await NewService(db).MarkAllReadAsync(userA);

        await using var verifyDb = PostgresFixture.CreateContext();
        Assert.Equal(0, await NewService(verifyDb).GetUnreadCountAsync(userA));
        Assert.Equal(1, await NewService(verifyDb).GetUnreadCountAsync(userB));
    }

    [Fact]
    public async Task MarkAllReadAsync_WhenNoUnread_IsANoOp()
    {
        var userId = Guid.NewGuid();
        await using var db = PostgresFixture.CreateContext();
        await NewService(db).MarkAllReadAsync(userId);
        Assert.Equal(0, await NewService(db).GetUnreadCountAsync(userId));
    }

    // Phase 12 — same overflow guard Phase 7.2.3 established in ProcessMonitoringQueryService,
    // now applied here too (see NotificationService.cs's own comment).
    [Fact]
    public async Task GetForUserAsync_ExtremelyLargePage_DoesNotThrow_ReturnsEmptyResult()
    {
        var userId = Guid.NewGuid();
        await using var setupDb = PostgresFixture.CreateContext();
        setupDb.Notifications.Add(NewNotification(userId));
        await setupDb.SaveChangesAsync();

        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).GetForUserAsync(userId, new NotificationQuery(Page: int.MaxValue, PageSize: 200));

        Assert.Empty(result.Items);
    }
}
