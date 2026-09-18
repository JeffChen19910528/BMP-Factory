using BPM.Application.Common;
using BPM.Application.ProcessMonitoring;
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

namespace BPM.Tests.ProcessMonitoring;

// Phase 7.2.1 — Process Monitoring Query + List. Setup helpers mirror
// DashboardQueryServiceTests.cs/SlaSchedulerTests.cs's own style (self-contained per file).
[Collection("Postgres")]
public class ProcessMonitoringQueryServiceTests
{
    private const string AdministratorRole = "Administrator";

    private static BPM.Infrastructure.Services.ProcessMonitoringQueryService NewQueryService(BpmDbContext db) => new(db);

    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static SlaPolicyService NewSlaService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new CreateSlaPolicyRequestValidator(), new UpdateSlaPolicyRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);

    private static Engine.SlaProcessor NewSlaProcessor(BpmDbContext db, FakeClock clock) =>
        new(db, clock, Options.Create(new Engine.SlaSchedulerSettings { Enabled = true, BatchSize = 50 }));

    private static async Task<Guid> CreateUserAsync(BpmDbContext db, string? displayName = null)
    {
        var user = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = displayName ?? "Test User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true };
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

    private static async Task<Guid> CreateProcessDefinitionAsync(BpmDbContext db, string? name = null)
    {
        var definition = await NewDefinitionService(db).CreateAsync(new CreateProcessDefinitionRequest($"mon-{Guid.NewGuid():N}", name ?? "Monitoring Test", null, null));
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

    // ---- Empty result ----

    [Fact]
    public async Task Empty_NoProcesses_ReturnsEmptyPage()
    {
        await using var db = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(db);

        var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: Guid.NewGuid()));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    // ---- Authorization ----

    [Fact]
    public async Task NormalUser_SeesOnlyOwnAuthorizedProcess_NotUnrelatedUsersProcess()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var processIdA = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdA, userA);
        var instanceA = await StartAsync(processIdA, userA);

        var processIdB = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdB, userB);
        await StartAsync(processIdB, userB);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(userA, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processIdA, PageSize: 50));

        Assert.Single(result.Items);
        Assert.Equal(instanceA, result.Items[0].ProcessInstanceId);

        var resultUnfiltered = await NewQueryService(db).GetAsync(userA, Array.Empty<string>(), new ProcessMonitoringQuery(PageSize: 200));
        Assert.DoesNotContain(resultUnfiltered.Items, i => i.ProcessDefinitionId == processIdB);
    }

    [Fact]
    public async Task Administrator_SeesSystemWide_IncludingUnrelatedUsersProcess()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var processIdA = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdA, userA);
        var instanceA = await StartAsync(processIdA, userA);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(Guid.NewGuid(), new[] { AdministratorRole }, new ProcessMonitoringQuery(ProcessDefinitionId: processIdA, PageSize: 50));

        Assert.Contains(result.Items, i => i.ProcessInstanceId == instanceA);
    }

    // ---- Filter/initiatorId/search cannot bypass authorization (IDOR) ----

    [Fact]
    public async Task InitiatorIdFilter_CannotExposeUnauthorizedUsersProcess()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var processIdA = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdA, userA);
        await StartAsync(processIdA, userA); // userA initiates and is assignee -> authorized only for userA.

        await using var db = PostgresFixture.CreateContext();
        // userB queries filtered to userA's own initiatorId — must return nothing, since userB is
        // not authorized for any of userA's process instances.
        var result = await NewQueryService(db).GetAsync(userB, Array.Empty<string>(), new ProcessMonitoringQuery(InitiatorId: userA, ProcessDefinitionId: processIdA));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task SearchFilter_CannotExposeUnauthorizedUsersProcess()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var uniqueName = $"SecretProcess-{Guid.NewGuid():N}";
        var processIdA = await CreateProcessDefinitionAsync(setupDb, uniqueName);
        await PublishSingleUserTaskAsync(processIdA, userA);
        await StartAsync(processIdA, userA);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(userB, Array.Empty<string>(), new ProcessMonitoringQuery(Search: uniqueName));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ProcessDefinitionIdFilter_CannotExposeUnauthorizedUsersProcess()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var processIdA = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdA, userA);
        await StartAsync(processIdA, userA);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(userB, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processIdA));

        Assert.Empty(result.Items);
    }

    // ---- Status filter ----

    [Fact]
    public async Task StatusFilter_ReturnsOnlyMatchingStatus()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        var runningInstance = await StartAsync(processId, user);
        var completedInstance = await StartAsync(processId, user);

        await using var taskDb = PostgresFixture.CreateContext();
        var completedTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == completedInstance);
        await NewEngine(taskDb).CompleteTaskAsync(completedTask.Id, user, Array.Empty<string>());

        await using var db = PostgresFixture.CreateContext();
        var runningResult = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, Status: ProcessInstanceStatus.Running));
        var completedResult = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, Status: ProcessInstanceStatus.Completed));

        Assert.Contains(runningResult.Items, i => i.ProcessInstanceId == runningInstance);
        Assert.DoesNotContain(runningResult.Items, i => i.ProcessInstanceId == completedInstance);
        Assert.Contains(completedResult.Items, i => i.ProcessInstanceId == completedInstance);
        Assert.DoesNotContain(completedResult.Items, i => i.ProcessInstanceId == runningInstance);
    }

    // ---- Search ----

    [Fact]
    public async Task Search_MatchesProcessDefinitionName_CaseInsensitive()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var uniqueName = $"PurchaseRequest-{Guid.NewGuid():N}";
        var processId = await CreateProcessDefinitionAsync(setupDb, uniqueName);
        await PublishSingleUserTaskAsync(processId, user);
        var instanceId = await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(Search: uniqueName.ToLowerInvariant()));

        Assert.Contains(result.Items, i => i.ProcessInstanceId == instanceId);
    }

    // ---- Pagination ----

    [Fact]
    public async Task Pagination_ReturnsCorrectPageAndTotalCount()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        for (var i = 0; i < 5; i++)
        {
            await StartAsync(processId, user);
        }

        await using var db = PostgresFixture.CreateContext();
        var page1 = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, Page: 1, PageSize: 2));
        var page2 = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, Page: 2, PageSize: 2));

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(2, page2.Items.Count);
        Assert.Empty(page1.Items.Select(i => i.ProcessInstanceId).Intersect(page2.Items.Select(i => i.ProcessInstanceId)));
    }

    // ---- Current task semantics ----

    [Fact]
    public async Task CurrentTask_PlainUserTask_ResolvesNameStatusAndAssignee()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb, "Amy Assignee");
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        var instanceId = await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId));
        var item = result.Items.Single(i => i.ProcessInstanceId == instanceId);

        Assert.NotNull(item.CurrentTaskId);
        Assert.Equal("Review", item.CurrentTaskName);
        Assert.Equal(TaskInstanceStatus.Pending, item.CurrentTaskStatus);
        Assert.False(item.CurrentTaskIsApprovalTask);
        Assert.Equal("Amy Assignee", item.CurrentTaskAssigneeDisplay);
        Assert.Equal(1, item.ActiveTaskCount);
    }

    [Fact]
    public async Task CurrentTask_ApprovalTask_ShowsCandidateCount_NeverAnArbitrarySingleCandidate()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var approverA = await CreateUserAsync(setupDb);
        var approverB = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, approverA, approverB);
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishApprovalTaskAsync(processId, ApprovalPolicy.All, role);
        var instanceId = await StartAsync(processId, initiator);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(initiator, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId));
        var item = result.Items.Single(i => i.ProcessInstanceId == instanceId);

        Assert.True(item.CurrentTaskIsApprovalTask);
        Assert.Equal("Pending approval (2 candidates)", item.CurrentTaskAssigneeDisplay);
    }

    [Fact]
    public async Task CompletedProcess_HasNoCurrentTask_ZeroActiveTaskCount()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        var instanceId = await StartAsync(processId, user);

        await using var taskDb = PostgresFixture.CreateContext();
        var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId);
        await NewEngine(taskDb).CompleteTaskAsync(task.Id, user, Array.Empty<string>());

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId));
        var item = result.Items.Single(i => i.ProcessInstanceId == instanceId);

        Assert.Null(item.CurrentTaskId);
        Assert.Equal(0, item.ActiveTaskCount);
        Assert.Equal(ProcessInstanceStatus.Completed, item.Status);
    }

    // ---- SLA aggregation (reuses real Phase 6.4 SlaProcessor state, never recomputed) ----

    [Fact]
    public async Task SlaStatus_ReflectsAuthoritativeSchedulerState_ActiveWarningOverdue()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));
        await PublishSingleUserTaskAsync(processId, user);
        var instanceId = await StartAsync(processId, user);

        await using var taskDb = PostgresFixture.CreateContext();
        var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId);
        var sla = await taskDb.TaskSlas.SingleAsync(s => s.TaskInstanceId == task.Id);

        await using (var db = PostgresFixture.CreateContext())
        {
            var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId));
            var item = result.Items.Single(i => i.ProcessInstanceId == instanceId);
            Assert.Equal(ProcessMonitoringSlaStatus.Active, item.SlaStatus);
            Assert.Equal(sla.DueAt, item.SlaDueAt);
        }

        await using (var db = PostgresFixture.CreateContext())
        {
            await NewSlaProcessor(db, new FakeClock { UtcNow = sla.WarningAt }).ProcessBatchAsync();
        }
        await using (var db = PostgresFixture.CreateContext())
        {
            var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, SlaStatus: ProcessMonitoringSlaStatus.Warning));
            Assert.Contains(result.Items, i => i.ProcessInstanceId == instanceId);
        }

        await using (var db = PostgresFixture.CreateContext())
        {
            await NewSlaProcessor(db, new FakeClock { UtcNow = sla.DueAt }).ProcessBatchAsync();
        }
        await using (var db = PostgresFixture.CreateContext())
        {
            var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, SlaStatus: ProcessMonitoringSlaStatus.Overdue));
            Assert.Contains(result.Items, i => i.ProcessInstanceId == instanceId);
            var notWarning = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, SlaStatus: ProcessMonitoringSlaStatus.Warning));
            Assert.DoesNotContain(notWarning.Items, i => i.ProcessInstanceId == instanceId);
        }
    }

    [Fact]
    public async Task NoApplicableSlaPolicy_SlaStatusIsNull()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        var instanceId = await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId));
        var item = result.Items.Single(i => i.ProcessInstanceId == instanceId);

        Assert.Null(item.SlaStatus);
        Assert.Null(item.SlaDueAt);
    }

    // ---- Sorting / deterministic ordering ----

    [Fact]
    public async Task Sorting_ByStartedAtAscending_OrdersCorrectly()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        var first = await StartAsync(processId, user);
        var second = await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, SortBy: ProcessMonitoringSortBy.StartedAt, SortDirection: SortDirection.Ascending, PageSize: 50));

        var ids = result.Items.Select(i => i.ProcessInstanceId).ToList();
        Assert.True(ids.IndexOf(first) < ids.IndexOf(second));
    }

    [Fact]
    public async Task InvalidSortBy_FallsBackToDefault_NeverThrows()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        // An out-of-range enum value simulates an unrecognized/invalid SortBy from a malformed
        // query string — must degrade to the default sort, never throw.
        var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, SortBy: (ProcessMonitoringSortBy)999));

        Assert.NotEmpty(result.Items);
    }

    [Fact]
    public async Task InvalidPageSize_FallsBackToDefault()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, PageSize: -5, Page: 0));

        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
    }

    // Phase 7.2.3 hardening — Part 18: "avoid integer overflow." Page has no upper clamp today;
    // (page - 1) * pageSize is plain `int` arithmetic, so a sufficiently large Page could
    // overflow and wrap to a negative Skip() argument. This test reproduces that exact input
    // against the real query service and real PostgreSQL to determine whether it is a genuine,
    // exploitable defect (an unhandled exception/500) or already safely handled.
    [Fact]
    public async Task ExtremelyLargePage_DoesNotThrow_ReturnsEmptyResult()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAsync(user, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId, Page: int.MaxValue, PageSize: 200));

        // Whatever the correct behavior turns out to be, it must not throw and must not return
        // another page's/user's data — an empty result for an absurd page number is safe.
        Assert.Empty(result.Items);
    }

    private class FakeClock : BPM.Application.Common.IClock
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
