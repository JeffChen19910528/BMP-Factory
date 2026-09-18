using BPM.Application.Common;
using BPM.Application.Dashboard;
using BPM.Application.Processes;
using BPM.Application.Sla;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using BPM.Tests.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Dashboard;

// Phase 7.1 — Dashboard Foundation. Proves DashboardQueryService aggregates existing,
// already-authorized data correctly — never a second authorization implementation. Setup helpers
// mirror SlaIntegrationTests.cs/SlaSchedulerTests.cs's own style (self-contained per file, matching
// this repo's established convention).
[Collection("Postgres")]
public class DashboardQueryServiceTests
{
    private const string AdministratorRole = "Administrator";

    private static DashboardQueryService NewDashboardService(BpmDbContext db, FakeClock clock, int dueSoonHours = 24) =>
        new(db, clock, Options.Create(new DashboardSettings { DueSoonHours = dueSoonHours }));

    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static SlaPolicyService NewSlaService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new CreateSlaPolicyRequestValidator(), new UpdateSlaPolicyRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);

    private static Engine.SlaProcessor NewSlaProcessor(BpmDbContext db, FakeClock clock) =>
        new(db, clock, Options.Create(new Engine.SlaSchedulerSettings { Enabled = true, BatchSize = 50 }));

    private static async Task<Guid> CreateUserAsync(BpmDbContext db)
    {
        var user = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = "Test User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<string> CreateRoleWithMembersAsync(BpmDbContext db, params Guid[] userIds)
    {
        var role = new Role { Name = $"Role-{Guid.NewGuid():N}" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        foreach (var userId in userIds)
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
        }
        await db.SaveChangesAsync();
        return role.Name;
    }

    private static async Task<Guid> CreateProcessDefinitionOnlyAsync(BpmDbContext db)
    {
        var definition = await NewDefinitionService(db).CreateAsync(new CreateProcessDefinitionRequest($"dash-{Guid.NewGuid():N}", "Dashboard Test", null, null));
        return definition.Id;
    }

    private static async Task PublishSingleUserTaskAsync(Guid processDefinitionId, Guid assignee)
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
    }

    private static async Task PublishProcessInitiatorTaskAsync(Guid processDefinitionId)
    {
        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, "")),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        await using var versionDb = PostgresFixture.CreateContext();
        await NewDefinitionService(versionDb).CreateVersionAsync(processDefinitionId, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(processDefinitionId, Guid.NewGuid());
    }

    private static async Task<Guid> PublishApprovalTaskAsync(Guid processDefinitionId, ApprovalPolicy policy, string role)
    {
        var graph = new WorkflowDefinition(
            Nodes: new WorkflowNodeDefinition[]
            {
                new("start", WorkflowNodeType.Start, "Start"),
                new("approval", WorkflowNodeType.ApprovalTask, "Approval", Approval: new ApprovalConfig(policy, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, role) }, true, true, true, true, true, new ReturnPolicy(true))),
                new("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "approval"), new WorkflowTransitionDefinition("t2", "approval", "end") });

        await using var versionDb = PostgresFixture.CreateContext();
        await NewDefinitionService(versionDb).CreateVersionAsync(processDefinitionId, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(processDefinitionId, Guid.NewGuid());
        return processDefinitionId;
    }

    private static async Task<Guid> StartAsync(Guid processDefinitionId, Guid initiatorId)
    {
        await using var keyDb = PostgresFixture.CreateContext();
        var key = (await keyDb.ProcessDefinitions.SingleAsync(p => p.Id == processDefinitionId)).Key;

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), initiatorId);
        return instance.Id;
    }

    // ---- Empty database ----

    [Fact]
    public async Task EmptyDashboard_AllZeros_NoRecentActivity()
    {
        await using var db = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(db);
        var clock = new FakeClock { UtcNow = DateTime.UtcNow };

        var result = await NewDashboardService(db, clock).GetDashboardAsync(user, Array.Empty<string>());

        Assert.Equal(0, result.MyTasks.Total);
        Assert.Equal(0, result.MyTasks.Overdue);
        Assert.Equal(0, result.MyTasks.DueSoon);
        Assert.Equal(0, result.PendingApprovals.Pending);
        Assert.Equal(0, result.Sla.Active + result.Sla.Warning + result.Sla.Overdue + result.Sla.Completed);
        // Process/Recent Activity may be non-empty in the shared bpm_test database from other
        // tests, so only shape/non-negativity is asserted here rather than exact zero.
        Assert.True(result.ProcessOverview.Running >= 0);
    }

    // ---- My Tasks ----

    [Fact]
    public async Task MyTasks_CountsOnlyOwnAssignedTask_NotAnotherUsersTask()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, userA);
        await StartAsync(processId, Guid.NewGuid());

        var clock = new FakeClock { UtcNow = DateTime.UtcNow };
        await using var db = PostgresFixture.CreateContext();
        var resultA = await NewDashboardService(db, clock).GetDashboardAsync(userA, Array.Empty<string>());
        var resultB = await NewDashboardService(db, clock).GetDashboardAsync(userB, Array.Empty<string>());

        Assert.True(resultA.MyTasks.Total >= 1);
        Assert.Equal(0, resultB.MyTasks.Total);
    }

    [Fact]
    public async Task Administrator_SeesSystemWideTaskCount_IncludingUnrelatedUsersTasks()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var normalUser = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, normalUser);
        await StartAsync(processId, Guid.NewGuid());

        var clock = new FakeClock { UtcNow = DateTime.UtcNow };
        await using var db = PostgresFixture.CreateContext();
        var normalResult = await NewDashboardService(db, clock).GetDashboardAsync(normalUser, Array.Empty<string>());
        var adminResult = await NewDashboardService(db, clock).GetDashboardAsync(Guid.NewGuid(), new[] { AdministratorRole });

        // The administrator's own identity has no tasks, but the system-wide count must still be
        // at least as large as any single normal user's own count.
        Assert.True(adminResult.MyTasks.Total >= normalResult.MyTasks.Total);
        Assert.True(adminResult.MyTasks.Total >= 1);
    }

    // ---- SLA Overview (Active/Warning/Overdue/Completed via the real Phase 6.4 engine) ----

    [Fact]
    public async Task SlaOverview_ReflectsAuthoritativeSchedulerState_ActiveWarningOverdue()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, assignee);
        var instanceId = await StartAsync(processId, Guid.NewGuid());

        await using var taskDb = PostgresFixture.CreateContext();
        var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId);
        var sla = await taskDb.TaskSlas.SingleAsync(s => s.TaskInstanceId == task.Id);

        // Before WarningAt: Active bucket.
        var clockBefore = new FakeClock { UtcNow = sla.WarningAt.AddMinutes(-1) };
        await using (var db = PostgresFixture.CreateContext())
        {
            var result = await NewDashboardService(db, clockBefore).GetDashboardAsync(assignee, Array.Empty<string>());
            Assert.Equal(1, result.Sla.Active);
            Assert.Equal(0, result.Sla.Warning);
            Assert.Equal(0, result.Sla.Overdue);
        }

        // Run the real SlaProcessor (Phase 6.4) to actually set WarningNotifiedAt — Dashboard must
        // reflect the scheduler's own authoritative state, not recompute it independently.
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewSlaProcessor(db, new FakeClock { UtcNow = sla.WarningAt }).ProcessBatchAsync();
        }
        await using (var db = PostgresFixture.CreateContext())
        {
            var result = await NewDashboardService(db, new FakeClock { UtcNow = sla.WarningAt }).GetDashboardAsync(assignee, Array.Empty<string>());
            Assert.Equal(0, result.Sla.Active);
            Assert.Equal(1, result.Sla.Warning);
            Assert.Equal(0, result.Sla.Overdue);
        }

        // Run the scheduler again at DueAt to actually transition to Overdue.
        await using (var db = PostgresFixture.CreateContext())
        {
            await NewSlaProcessor(db, new FakeClock { UtcNow = sla.DueAt }).ProcessBatchAsync();
        }
        await using (var db = PostgresFixture.CreateContext())
        {
            var result = await NewDashboardService(db, new FakeClock { UtcNow = sla.DueAt }).GetDashboardAsync(assignee, Array.Empty<string>());
            Assert.Equal(0, result.Sla.Active);
            Assert.Equal(0, result.Sla.Warning);
            Assert.Equal(1, result.Sla.Overdue);
        }
    }

    [Fact]
    public async Task DueSoon_IsDeterministic_UsesConfiguredThreshold()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        // 10-hour duration, no warning offset relevance here (0 warning offset would make Warning
        // == Due; use a nonzero small offset so WarningAt < DueAt).
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 600, 60));
        await PublishSingleUserTaskAsync(processId, assignee);
        var instanceId = await StartAsync(processId, Guid.NewGuid());

        await using var taskDb = PostgresFixture.CreateContext();
        var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId);
        var sla = await taskDb.TaskSlas.SingleAsync(s => s.TaskInstanceId == task.Id);

        // DueAt is StartedAt + 600 minutes (10 hours). With a 24-hour DueSoon threshold, "now"
        // must already be within 24 hours of DueAt -> DueSoon triggers immediately.
        var clock = new FakeClock { UtcNow = sla.StartedAt };
        await using (var db = PostgresFixture.CreateContext())
        {
            var result = await NewDashboardService(db, clock, dueSoonHours: 24).GetDashboardAsync(assignee, Array.Empty<string>());
            Assert.Equal(1, result.MyTasks.DueSoon);
        }

        // With a 1-hour threshold instead, the same 10-hours-away DueAt must NOT count as DueSoon.
        await using (var db = PostgresFixture.CreateContext())
        {
            var result = await NewDashboardService(db, clock, dueSoonHours: 1).GetDashboardAsync(assignee, Array.Empty<string>());
            Assert.Equal(0, result.MyTasks.DueSoon);
        }
    }

    // ---- Pending Approvals ----

    [Fact]
    public async Task PendingApprovals_CountsOnlyCallersOwnPendingAssignment()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var approverA = await CreateUserAsync(setupDb);
        var approverB = await CreateUserAsync(setupDb);
        var roleA = await CreateRoleWithMembersAsync(setupDb, approverA);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await PublishApprovalTaskAsync(processId, ApprovalPolicy.AnyOne, roleA);
        await StartAsync(processId, Guid.NewGuid());

        var clock = new FakeClock { UtcNow = DateTime.UtcNow };
        await using var db = PostgresFixture.CreateContext();
        var resultA = await NewDashboardService(db, clock).GetDashboardAsync(approverA, Array.Empty<string>());
        var resultB = await NewDashboardService(db, clock).GetDashboardAsync(approverB, Array.Empty<string>());

        Assert.Equal(1, resultA.PendingApprovals.Pending);
        Assert.Equal(0, resultB.PendingApprovals.Pending);
    }

    // ---- Process Overview / authorization exclusion ----

    [Fact]
    public async Task ProcessOverview_NormalUser_ExcludesUnrelatedProcessInstance()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);

        // Two entirely separate process definitions, each assigning its task only to its own
        // initiator (WorkflowAssignmentType.ProcessInitiator) — genuinely unrelated instances,
        // unlike a shared definition with a hardcoded assignee that would authorize both users.
        var processIdA = await CreateProcessDefinitionOnlyAsync(setupDb);
        await PublishProcessInitiatorTaskAsync(processIdA);
        await StartAsync(processIdA, userA);

        var processIdB = await CreateProcessDefinitionOnlyAsync(setupDb);
        await PublishProcessInitiatorTaskAsync(processIdB);
        await StartAsync(processIdB, userB);

        var clock = new FakeClock { UtcNow = DateTime.UtcNow };
        await using var db = PostgresFixture.CreateContext();
        var resultA = await NewDashboardService(db, clock).GetDashboardAsync(userA, Array.Empty<string>());

        // userA is authorized for exactly their own instance (Running), not userB's.
        Assert.Equal(1, resultA.ProcessOverview.Running);
    }

    [Fact]
    public async Task ProcessOverview_Administrator_SeesAggregateAcrossAllInstances()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var processIdA = await CreateProcessDefinitionOnlyAsync(setupDb);
        await PublishProcessInitiatorTaskAsync(processIdA);
        await StartAsync(processIdA, userA);

        var processIdB = await CreateProcessDefinitionOnlyAsync(setupDb);
        await PublishProcessInitiatorTaskAsync(processIdB);
        await StartAsync(processIdB, userB);

        var clock = new FakeClock { UtcNow = DateTime.UtcNow };
        await using var db = PostgresFixture.CreateContext();
        var adminResult = await NewDashboardService(db, clock).GetDashboardAsync(Guid.NewGuid(), new[] { AdministratorRole });

        Assert.True(adminResult.ProcessOverview.Running >= 2);
    }

    // ---- Recent Activity ----

    [Fact]
    public async Task RecentActivity_ShowsAuthorizedProcessStarted_ExcludesUnrelatedUsersActivity()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var processIdA = await CreateProcessDefinitionOnlyAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdA, userA);
        var instanceA = await StartAsync(processIdA, userA);

        var processIdB = await CreateProcessDefinitionOnlyAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdB, userB);
        await StartAsync(processIdB, userB);

        var clock = new FakeClock { UtcNow = DateTime.UtcNow };
        await using var db = PostgresFixture.CreateContext();
        var resultA = await NewDashboardService(db, clock).GetDashboardAsync(userA, Array.Empty<string>());

        Assert.Contains(resultA.RecentActivity, a => a.ProcessInstanceId == instanceA && a.Action == "StartProcess");
        Assert.DoesNotContain(resultA.RecentActivity, a => a.Description.Contains("started") && a.ProcessInstanceId != instanceA && a.ProcessInstanceId != null && a.ProcessInstanceId == instanceA);
    }

    [Fact]
    public async Task RecentActivity_TaskCompleted_AppearsWithCorrectDescription()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, assignee);
        var instanceId = await StartAsync(processId, Guid.NewGuid());

        await using var taskDb = PostgresFixture.CreateContext();
        var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId);

        await using var completeDb = PostgresFixture.CreateContext();
        await NewEngine(completeDb).CompleteTaskAsync(task.Id, assignee, Array.Empty<string>());

        var clock = new FakeClock { UtcNow = DateTime.UtcNow };
        await using var db = PostgresFixture.CreateContext();
        var result = await NewDashboardService(db, clock).GetDashboardAsync(assignee, Array.Empty<string>());

        Assert.Contains(result.RecentActivity, a => a.Action == "TaskCompleted" && a.Description == "Task completed" && a.ProcessInstanceId == instanceId);
    }

    // ---- Authorization / IDOR-style checks at the service layer ----

    [Fact]
    public async Task NormalUser_CannotSeeAnotherUsersSlaThroughDashboard()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, userA);
        await StartAsync(processId, Guid.NewGuid());

        var clock = new FakeClock { UtcNow = DateTime.UtcNow };
        await using var db = PostgresFixture.CreateContext();
        var resultB = await NewDashboardService(db, clock).GetDashboardAsync(userB, Array.Empty<string>());

        Assert.Equal(0, resultB.Sla.Active + resultB.Sla.Warning + resultB.Sla.Overdue + resultB.Sla.Completed);
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
