using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Sla;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Workflow;

// Phase 6.4 — SLA Scheduler / Warning / Overdue / Escalation. Proves SlaProcessor's business logic
// deterministically (FakeClock, no Thread.Sleep, no real-time waiting) against the same real
// Postgres database every other engine integration test in this repo uses. Setup helpers mirror
// SlaIntegrationTests.cs's own style deliberately (this file is self-contained rather than sharing
// private helpers across files, matching this repo's existing per-file convention).
[Collection("Postgres")]
public class SlaSchedulerTests
{
    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static SlaPolicyService NewSlaService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new CreateSlaPolicyRequestValidator(), new UpdateSlaPolicyRequestValidator());

    private static EscalationPolicyService NewEscalationService(BpmDbContext db) =>
        new(db, new CreateEscalationPolicyRequestValidator(), new UpdateEscalationPolicyRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);

    private static Engine.SlaProcessor NewProcessor(BpmDbContext db, FakeClock clock, int batchSize = 50) =>
        new(db, clock, Options.Create(new Engine.SlaSchedulerSettings { Enabled = true, BatchSize = batchSize }));

    private static Engine.SlaProcessor NewDisabledProcessor(BpmDbContext db, FakeClock clock) =>
        new(db, clock, Options.Create(new Engine.SlaSchedulerSettings { Enabled = false }));

    private static async Task<Guid> CreateUserAsync(BpmDbContext db)
    {
        var user = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = "Test User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> CreateProcessDefinitionOnlyAsync(BpmDbContext db)
    {
        var definition = await NewDefinitionService(db).CreateAsync(new CreateProcessDefinitionRequest($"sched-{Guid.NewGuid():N}", "Scheduler Test", null, null));
        return definition.Id;
    }

    private static async Task<Guid> PublishSingleUserTaskAsync(Guid processDefinitionId, Guid assignee)
    {
        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.User, assignee.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        await using var versionDb = PostgresFixture.CreateContext();
        await NewDefinitionService(versionDb).CreateVersionAsync(processDefinitionId, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(processDefinitionId, Guid.NewGuid());
        return processDefinitionId;
    }

    private static async Task<Guid> StartAndGetTaskIdAsync(Guid processDefinitionId, Guid initiatorId)
    {
        await using var keyDb = PostgresFixture.CreateContext();
        var key = (await keyDb.ProcessDefinitions.SingleAsync(p => p.Id == processDefinitionId)).Key;

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), initiatorId);

        await using var taskDb = PostgresFixture.CreateContext();
        return (await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id)).Id;
    }

    private static async Task<TaskSla> GetSlaAsync(Guid taskInstanceId)
    {
        await using var db = PostgresFixture.CreateContext();
        return await db.TaskSlas.SingleAsync(s => s.TaskInstanceId == taskInstanceId);
    }

    private static async Task<int> CountNotificationsAsync(Guid recipientUserId, NotificationType type, string relatedEntityId)
    {
        await using var db = PostgresFixture.CreateContext();
        return await db.Notifications.CountAsync(n => n.RecipientUserId == recipientUserId && n.Type == type && n.RelatedEntityId == relatedEntityId);
    }

    // ---- Scenario A: Warning ----

    [Fact]
    public async Task Warning_NotFiredBeforeWarningAt_FiresExactlyOnceAtOrAfter_NeverTwice()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, assignee);
        var taskId = await StartAndGetTaskIdAsync(processId, Guid.NewGuid());
        var slaBefore = await GetSlaAsync(taskId);

        var clock = new FakeClock { UtcNow = slaBefore.WarningAt.AddMinutes(-1) };
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewProcessor(db, clock).ProcessBatchAsync();
        }
        var slaStillActive = await GetSlaAsync(taskId);
        Assert.Null(slaStillActive.WarningNotifiedAt);
        Assert.Equal(0, await CountNotificationsAsync(assignee, NotificationType.SlaWarning, taskId.ToString()));

        clock.UtcNow = slaBefore.WarningAt;
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewProcessor(db, clock).ProcessBatchAsync();
        }
        var slaWarned = await GetSlaAsync(taskId);
        Assert.NotNull(slaWarned.WarningNotifiedAt);
        Assert.Equal(TaskSlaStatus.Active, slaWarned.Status);
        Assert.Equal(1, await CountNotificationsAsync(assignee, NotificationType.SlaWarning, taskId.ToString()));

        // Running again (even repeatedly) must never create a second warning notification.
        for (var i = 0; i < 3; i++)
        {
            await using var db = PostgresFixture.CreateContext();
            await NewProcessor(db, clock).ProcessBatchAsync();
        }
        Assert.Equal(1, await CountNotificationsAsync(assignee, NotificationType.SlaWarning, taskId.ToString()));
    }

    // ---- Scenario B: Overdue ----

    [Fact]
    public async Task Overdue_TransitionsExactlyOnce_NotifiesResponsibleAndInitiator_NeverDuplicated()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, assignee);
        var initiator = Guid.NewGuid();
        var taskId = await StartAndGetTaskIdAsync(processId, initiator);
        var slaBefore = await GetSlaAsync(taskId);

        var clock = new FakeClock { UtcNow = slaBefore.DueAt };
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewProcessor(db, clock).ProcessBatchAsync();
        }

        var sla = await GetSlaAsync(taskId);
        Assert.Equal(TaskSlaStatus.Overdue, sla.Status);
        Assert.NotNull(sla.OverdueAt);
        Assert.Equal(1, await CountNotificationsAsync(assignee, NotificationType.SlaOverdue, taskId.ToString()));
        Assert.Equal(1, await CountNotificationsAsync(initiator, NotificationType.SlaOverdue, taskId.ToString()));

        for (var i = 0; i < 3; i++)
        {
            await using var db = PostgresFixture.CreateContext();
            await NewProcessor(db, clock).ProcessBatchAsync();
        }
        Assert.Equal(1, await CountNotificationsAsync(assignee, NotificationType.SlaOverdue, taskId.ToString()));
        Assert.Equal(1, await CountNotificationsAsync(initiator, NotificationType.SlaOverdue, taskId.ToString()));
    }

    // ---- Scenario C: Completed Before Due ----

    [Fact]
    public async Task CompletedBeforeDue_NeverBecomesOverdue_NoOverdueNotification()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, assignee);
        var taskId = await StartAndGetTaskIdAsync(processId, Guid.NewGuid());
        var slaBefore = await GetSlaAsync(taskId);

        await using (var completeDb = PostgresFixture.CreateContext())
        {
            await NewEngine(completeDb).CompleteTaskAsync(taskId, assignee, Array.Empty<string>());
        }

        var clock = new FakeClock { UtcNow = slaBefore.DueAt.AddDays(1) };
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewProcessor(db, clock).ProcessBatchAsync();
        }

        var sla = await GetSlaAsync(taskId);
        Assert.Equal(TaskSlaStatus.Completed, sla.Status);
        Assert.Null(sla.OverdueAt);
        Assert.Equal(0, await CountNotificationsAsync(assignee, NotificationType.SlaOverdue, taskId.ToString()));
    }

    // ---- Part Z: never process Cancelled either (defensive — TaskSlaStatus.Cancelled is still
    // reserved/unreachable, but the candidate query's WHERE Status == Active/Overdue must not
    // accidentally also match a hypothetical future Cancelled row). ----

    [Fact]
    public async Task DisabledScheduler_NeverTransitionsOrNotifies()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, assignee);
        var taskId = await StartAndGetTaskIdAsync(processId, Guid.NewGuid());
        var slaBefore = await GetSlaAsync(taskId);

        var clock = new FakeClock { UtcNow = slaBefore.DueAt.AddDays(1) };
        await using var db = PostgresFixture.CreateContext();
        var changed = await NewDisabledProcessor(db, clock).ProcessBatchAsync();

        Assert.Equal(0, changed);
        var sla = await GetSlaAsync(taskId);
        Assert.Equal(TaskSlaStatus.Active, sla.Status);
    }

    // ---- Scenario I: Task Completion Race ----

    [Fact]
    public async Task CompletionRace_SchedulerWinsOverdueFirst_CompletionStillEndsAsCompleted()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, assignee);
        var taskId = await StartAndGetTaskIdAsync(processId, Guid.NewGuid());
        var slaBefore = await GetSlaAsync(taskId);

        // Scheduler claims Overdue first...
        var clock = new FakeClock { UtcNow = slaBefore.DueAt };
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewProcessor(db, clock).ProcessBatchAsync();
        }
        Assert.Equal(TaskSlaStatus.Overdue, (await GetSlaAsync(taskId)).Status);

        // ...but the assignee still completes the task immediately afterward. Final state must be
        // Completed, not stuck at Overdue — Overdue is preserved only as history (OverdueAt).
        await using (var completeDb = PostgresFixture.CreateContext())
        {
            await NewEngine(completeDb).CompleteTaskAsync(taskId, assignee, Array.Empty<string>());
        }

        var finalSla = await GetSlaAsync(taskId);
        Assert.Equal(TaskSlaStatus.Completed, finalSla.Status);
        Assert.NotNull(finalSla.OverdueAt); // historical fact preserved.
        Assert.NotNull(finalSla.CompletedAt);
    }

    [Fact]
    public async Task CompletionRace_CompletionWinsFirst_SchedulerNeverMarksItOverdue()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, assignee);
        var taskId = await StartAndGetTaskIdAsync(processId, Guid.NewGuid());
        var slaBefore = await GetSlaAsync(taskId);

        // Task is completed before the scheduler ever evaluates it.
        await using (var completeDb = PostgresFixture.CreateContext())
        {
            await NewEngine(completeDb).CompleteTaskAsync(taskId, assignee, Array.Empty<string>());
        }

        var clock = new FakeClock { UtcNow = slaBefore.DueAt };
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewProcessor(db, clock).ProcessBatchAsync();
        }

        var sla = await GetSlaAsync(taskId);
        Assert.Equal(TaskSlaStatus.Completed, sla.Status);
        Assert.Null(sla.OverdueAt);
    }

    // ---- Scenario E: Escalation ----

    [Fact]
    public async Task Escalation_FiresExactlyOnceAtEscalationAt_NotBeforeOrTwice()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var manager = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await NewEscalationService(setupDb).CreateAsync(new CreateEscalationPolicyRequest(processId, "task", true, 120, WorkflowAssignmentType.User, manager.ToString()));
        await PublishSingleUserTaskAsync(processId, assignee);
        var taskId = await StartAndGetTaskIdAsync(processId, Guid.NewGuid());
        var slaBefore = await GetSlaAsync(taskId);

        // Become Overdue first (escalation is computed relative to OverdueAt, not DueAt directly).
        var clock = new FakeClock { UtcNow = slaBefore.DueAt };
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewProcessor(db, clock).ProcessBatchAsync();
        }
        var overdueSla = await GetSlaAsync(taskId);
        Assert.NotNull(overdueSla.EscalationAt);
        Assert.Null(overdueSla.EscalatedAt);

        // Before EscalationAt: nothing happens.
        clock.UtcNow = overdueSla.EscalationAt!.Value.AddMinutes(-1);
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewProcessor(db, clock).ProcessBatchAsync();
        }
        Assert.Null((await GetSlaAsync(taskId)).EscalatedAt);
        Assert.Equal(0, await CountNotificationsAsync(manager, NotificationType.SlaEscalated, taskId.ToString()));

        // At EscalationAt: escalates exactly once.
        clock.UtcNow = overdueSla.EscalationAt.Value;
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewProcessor(db, clock).ProcessBatchAsync();
        }
        Assert.NotNull((await GetSlaAsync(taskId)).EscalatedAt);
        Assert.Equal(1, await CountNotificationsAsync(manager, NotificationType.SlaEscalated, taskId.ToString()));

        for (var i = 0; i < 3; i++)
        {
            await using var db = PostgresFixture.CreateContext();
            await NewProcessor(db, clock).ProcessBatchAsync();
        }
        Assert.Equal(1, await CountNotificationsAsync(manager, NotificationType.SlaEscalated, taskId.ToString()));
    }

    [Fact]
    public async Task NoEscalationPolicy_OverdueNeverEscalates()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        // No EscalationPolicy created for this node.
        await PublishSingleUserTaskAsync(processId, assignee);
        var taskId = await StartAndGetTaskIdAsync(processId, Guid.NewGuid());
        var slaBefore = await GetSlaAsync(taskId);

        var clock = new FakeClock { UtcNow = slaBefore.DueAt.AddDays(30) };
        for (var i = 0; i < 2; i++)
        {
            await using var db = PostgresFixture.CreateContext();
            await NewProcessor(db, clock).ProcessBatchAsync();
        }

        var sla = await GetSlaAsync(taskId);
        Assert.Equal(TaskSlaStatus.Overdue, sla.Status);
        Assert.Null(sla.EscalationAt);
        Assert.Null(sla.EscalatedAt);
    }

    // ---- Scenarios F/G/H: Concurrent workers race on the same TaskSla ----

    [Fact]
    public async Task ConcurrentWarningClaim_ExactlyOneNotificationCreated()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, assignee);
        var taskId = await StartAndGetTaskIdAsync(processId, Guid.NewGuid());
        var slaBefore = await GetSlaAsync(taskId);

        var now = slaBefore.WarningAt;
        await using var dbA = PostgresFixture.CreateContext();
        await using var dbB = PostgresFixture.CreateContext();
        var processorA = NewProcessor(dbA, new FakeClock { UtcNow = now });
        var processorB = NewProcessor(dbB, new FakeClock { UtcNow = now });

        await Task.WhenAll(processorA.ProcessBatchAsync(), processorB.ProcessBatchAsync());

        Assert.Equal(1, await CountNotificationsAsync(assignee, NotificationType.SlaWarning, taskId.ToString()));
        Assert.NotNull((await GetSlaAsync(taskId)).WarningNotifiedAt);
    }

    [Fact]
    public async Task ConcurrentOverdueClaim_ExactlyOneTransitionAndNotification()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, assignee);
        var initiator = Guid.NewGuid();
        var taskId = await StartAndGetTaskIdAsync(processId, initiator);
        var slaBefore = await GetSlaAsync(taskId);

        var now = slaBefore.DueAt;
        await using var dbA = PostgresFixture.CreateContext();
        await using var dbB = PostgresFixture.CreateContext();
        var processorA = NewProcessor(dbA, new FakeClock { UtcNow = now });
        var processorB = NewProcessor(dbB, new FakeClock { UtcNow = now });

        await Task.WhenAll(processorA.ProcessBatchAsync(), processorB.ProcessBatchAsync());

        Assert.Equal(TaskSlaStatus.Overdue, (await GetSlaAsync(taskId)).Status);
        Assert.Equal(1, await CountNotificationsAsync(assignee, NotificationType.SlaOverdue, taskId.ToString()));
        Assert.Equal(1, await CountNotificationsAsync(initiator, NotificationType.SlaOverdue, taskId.ToString()));
    }

    [Fact]
    public async Task ConcurrentEscalationClaim_ExactlyOneEscalation()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var manager = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await NewEscalationService(setupDb).CreateAsync(new CreateEscalationPolicyRequest(processId, "task", true, 0, WorkflowAssignmentType.User, manager.ToString()));
        await PublishSingleUserTaskAsync(processId, assignee);
        var taskId = await StartAndGetTaskIdAsync(processId, Guid.NewGuid());
        var slaBefore = await GetSlaAsync(taskId);

        // Zero-delay escalation policy: becomes overdue and escalation-eligible in the same tick.
        var now = slaBefore.DueAt;
        await using (var seedDb = PostgresFixture.CreateContext())
        {
            await NewProcessor(seedDb, new FakeClock { UtcNow = now }).ProcessBatchAsync();
        }
        Assert.Equal(1, await CountNotificationsAsync(manager, NotificationType.SlaEscalated, taskId.ToString()));

        // Racing two more processors at the same instant must not create a second escalation.
        await using var dbA = PostgresFixture.CreateContext();
        await using var dbB = PostgresFixture.CreateContext();
        var processorA = NewProcessor(dbA, new FakeClock { UtcNow = now });
        var processorB = NewProcessor(dbB, new FakeClock { UtcNow = now });
        await Task.WhenAll(processorA.ProcessBatchAsync(), processorB.ProcessBatchAsync());

        Assert.Equal(1, await CountNotificationsAsync(manager, NotificationType.SlaEscalated, taskId.ToString()));
    }

    // ---- Warning/Overdue recipient resolution reuses ApprovalAssignment semantics ----

    [Fact]
    public async Task ApprovalTask_Overdue_NotifiesOnlyCurrentlyActionableSequentialApprover()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var first = await CreateUserAsync(setupDb);
        var second = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 60, 30));

        var graph = new WorkflowDefinition(
            Nodes: new WorkflowNodeDefinition[]
            {
                new("start", WorkflowNodeType.Start, "Start"),
                new("approval", WorkflowNodeType.ApprovalTask, "Approval", Approval: new ApprovalConfig(
                    ApprovalPolicy.Sequential,
                    new[] { new WorkflowAssignment(WorkflowAssignmentType.User, first.ToString()), new WorkflowAssignment(WorkflowAssignmentType.User, second.ToString()) },
                    true, true, true, true, true, new ReturnPolicy(true))),
                new("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "approval"), new WorkflowTransitionDefinition("t2", "approval", "end") });

        await using (var versionDb = PostgresFixture.CreateContext())
        {
            await NewDefinitionService(versionDb).CreateVersionAsync(processId, new CreateProcessVersionRequest(graph));
        }
        await using (var publishDb = PostgresFixture.CreateContext())
        {
            await NewEngine(publishDb).PublishVersionAsync(processId, Guid.NewGuid());
        }

        var taskId = await StartAndGetTaskIdAsync(processId, Guid.NewGuid());
        var slaBefore = await GetSlaAsync(taskId);

        var clock = new FakeClock { UtcNow = slaBefore.DueAt };
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewProcessor(db, clock).ProcessBatchAsync();
        }

        // Sequential: only the first (Order 0) candidate is actionable yet, so only they get the
        // Overdue notification — the second candidate cannot act yet and is not notified.
        Assert.Equal(1, await CountNotificationsAsync(first, NotificationType.SlaOverdue, taskId.ToString()));
        Assert.Equal(0, await CountNotificationsAsync(second, NotificationType.SlaOverdue, taskId.ToString()));
    }

    private class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; }
    }

    private class FixedCurrentUser : ICurrentUserService
    {
        public FixedCurrentUser(Guid? userId) => UserId = userId;
        public Guid? UserId { get; }
        public Guid TenantId => Guid.Empty;
        public IReadOnlyCollection<string> Roles => Array.Empty<string>();
        public string? IpAddress => null;
        public string? UserAgent => null;
    }
}
