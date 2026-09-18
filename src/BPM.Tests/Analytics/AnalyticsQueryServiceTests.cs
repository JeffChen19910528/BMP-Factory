using BPM.Application.Analytics;
using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using BPM.Tests.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Analytics;

// Phase 7.4 — Analytics. Setup helpers mirror ReportQueryServiceTests.cs's own self-contained-
// per-file style. Duration-dependent tests set ProcessInstance/TaskInstance timestamps directly
// via the DbContext (rather than waiting on real elapsed time) for exact, deterministic
// assertions — the same approach the live E2E test cannot use (it goes through real HTTP over
// real elapsed time), so exact-hour assertions belong here, not in the live suite.
[Collection("Postgres")]
public class AnalyticsQueryServiceTests
{
    private const string AdministratorRole = "Administrator";

    private static AnalyticsQueryService NewAnalyticsService(BpmDbContext db) => new(db);

    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);

    private static async Task<Guid> CreateUserAsync(BpmDbContext db, string? displayName = null, Guid? departmentId = null)
    {
        var user = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = displayName ?? "Test User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true, DepartmentId = departmentId };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Guid> CreateProcessDefinitionAsync(BpmDbContext db, string? name = null)
    {
        var definition = await NewDefinitionService(db).CreateAsync(new CreateProcessDefinitionRequest($"an-{Guid.NewGuid():N}", name ?? "Analytics Test", null, null));
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

    private static async Task<Guid> StartAsync(Guid processDefinitionId, Guid initiatorId)
    {
        await using var keyDb = PostgresFixture.CreateContext();
        var key = (await keyDb.ProcessDefinitions.SingleAsync(p => p.Id == processDefinitionId)).Key;
        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), initiatorId);
        return instance.Id;
    }

    // ---- Process Volume + Duration ----

    [Fact]
    public async Task ProcessDuration_ExcludesRunningAndUsesExactHours()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        var completedInstance = await StartAsync(processId, user);
        await StartAsync(processId, user); // stays Running — must be excluded

        await using (var taskDb = PostgresFixture.CreateContext())
        {
            var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == completedInstance);
            await NewEngine(taskDb).CompleteTaskAsync(task.Id, user, Array.Empty<string>());
        }

        // Force a deterministic, exact 4-hour duration.
        await using (var patchDb = PostgresFixture.CreateContext())
        {
            var instance = await patchDb.ProcessInstances.SingleAsync(p => p.Id == completedInstance);
            instance.StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            instance.CompletedAt = new DateTime(2026, 1, 1, 4, 0, 0, DateTimeKind.Utc);
            await patchDb.SaveChangesAsync();
        }

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery(ProcessDefinitionId: processId));

        Assert.Equal(1, overview.ProcessDuration.SampleCount);
        Assert.Equal(4.0, overview.ProcessDuration.AverageHours);
        Assert.Equal(4.0, overview.ProcessDuration.MinHours);
        Assert.Equal(4.0, overview.ProcessDuration.MaxHours);
    }

    [Fact]
    public async Task ProcessDuration_NoCompletedInstances_ReturnsZeroSampleAndNullStats()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery(ProcessDefinitionId: processId));

        Assert.Equal(0, overview.ProcessDuration.SampleCount);
        Assert.Null(overview.ProcessDuration.AverageHours);
    }

    [Fact]
    public async Task VolumeTrend_BucketsStartedByDay()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        var instanceId = await StartAsync(processId, user);

        var fixedDay = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        await using (var patchDb = PostgresFixture.CreateContext())
        {
            var instance = await patchDb.ProcessInstances.SingleAsync(p => p.Id == instanceId);
            instance.StartedAt = fixedDay.AddHours(5);
            await patchDb.SaveChangesAsync();
        }

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery(ProcessDefinitionId: processId, Granularity: AnalyticsGranularity.Day));

        var bucket = overview.VolumeTrend.Single(v => v.BucketStart.Date == fixedDay.Date);
        Assert.Equal(1, bucket.Started);
    }

    // ---- Task Throughput / Duration ----

    [Fact]
    public async Task TaskDuration_UsesCreatedAtNotStartedAt_ExcludesIncomplete()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        var completedInstance = await StartAsync(processId, user);
        await StartAsync(processId, user); // task stays Pending — excluded

        await using (var taskDb = PostgresFixture.CreateContext())
        {
            var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == completedInstance);
            await NewEngine(taskDb).CompleteTaskAsync(task.Id, user, Array.Empty<string>());
        }

        await using (var patchDb = PostgresFixture.CreateContext())
        {
            var task = await patchDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == completedInstance);
            task.CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            task.CompletedAt = new DateTime(2026, 2, 1, 2, 0, 0, DateTimeKind.Utc);
            await patchDb.SaveChangesAsync();
        }

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery(ProcessDefinitionId: processId));

        Assert.Equal(1, overview.TaskDuration.SampleCount);
        Assert.Equal(2.0, overview.TaskDuration.AverageHours);
    }

    // ---- Node Analytics ----

    [Fact]
    public async Task NodeAnalytics_GroupsByProcessDefinitionAndNodeId_UsesSnapshotNodeName()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        var instanceId = await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery(ProcessDefinitionId: processId));

        var node = overview.NodeAnalytics.Single(n => n.NodeId == "task");
        Assert.Equal("Review", node.NodeName);
        Assert.Equal(1, node.Executions);
        _ = instanceId;
    }

    // Part 33/57 — republishing a new version must not change how an existing instance's node
    // names are reported (TaskInstance.NodeName is a creation-time snapshot, never re-resolved).
    [Fact]
    public async Task NodeAnalytics_RepublishingNewVersion_DoesNotAffectExistingInstanceNodeNames()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user); // node name "Review"
        var instanceId = await StartAsync(processId, user);

        // Republish a new version with a different node name for the same NodeId.
        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Renamed Review", new WorkflowAssignment(WorkflowAssignmentType.User, user.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });
        await using (var versionDb = PostgresFixture.CreateContext())
        {
            await NewDefinitionService(versionDb).CreateVersionAsync(processId, new CreateProcessVersionRequest(graph));
        }
        await using (var publishDb = PostgresFixture.CreateContext())
        {
            await NewEngine(publishDb).PublishVersionAsync(processId, Guid.NewGuid());
        }

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery(ProcessDefinitionId: processId));

        var node = overview.NodeAnalytics.Single(n => n.NodeId == "task");
        Assert.Equal("Review", node.NodeName); // the original instance's own snapshot, not "Renamed Review"
        _ = instanceId;
    }

    // ---- SLA Trend ----

    [Fact]
    public async Task SlaTrend_NoCompletedSla_ReturnsEmptyTrend()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery(ProcessDefinitionId: processId));

        Assert.Empty(overview.SlaTrend);
    }

    // ---- Date range / validation ----

    [Fact]
    public async Task InvalidDateRange_FromAfterTo_ThrowsBadRequest()
    {
        await using var db = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(db);

        await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery(From: DateTime.UtcNow, To: DateTime.UtcNow.AddDays(-1))));
    }

    [Fact]
    public async Task DateRange_ExcludesInstancesOutsideRange()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, user);
        await StartAsync(processId, user);

        await using var db = PostgresFixture.CreateContext();
        var future = DateTime.UtcNow.AddDays(1);
        var overview = await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery(ProcessDefinitionId: processId, From: future));

        Assert.Equal(0, overview.TotalProcesses);
    }

    // ---- Department filter ----

    [Fact]
    public async Task DepartmentFilter_ScopesByInitiatorDepartment()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var org = new Organization { Name = $"Org-{Guid.NewGuid():N}" };
        setupDb.Organizations.Add(org);
        await setupDb.SaveChangesAsync();
        var dept = new Department { Name = $"Dept-{Guid.NewGuid():N}", OrganizationId = org.Id };
        setupDb.Departments.Add(dept);
        await setupDb.SaveChangesAsync();

        var userA = await CreateUserAsync(setupDb, departmentId: dept.Id);
        var userB = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processId, userA);
        await StartAsync(processId, userA);
        var processIdB = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdB, userB);
        await StartAsync(processIdB, userB);

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(userA, new[] { AdministratorRole }, new AnalyticsQuery(DepartmentId: dept.Id));

        Assert.Equal(1, overview.TotalProcesses);
    }

    // ---- Authorization / IDOR / aggregate leakage ----

    [Fact]
    public async Task NormalUser_AggregatesOnlyReflectOwnAuthorizedScope()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var processIdA = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdA, userA);
        await StartAsync(processIdA, userA);

        var processIdB = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdB, userB);
        await StartAsync(processIdB, userB);
        await StartAsync(processIdB, userB);

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(userA, Array.Empty<string>(), new AnalyticsQuery(ProcessDefinitionId: processIdB));

        Assert.Equal(0, overview.TotalProcesses);
        Assert.Empty(overview.ProcessComparison);
        Assert.Empty(overview.NodeAnalytics);
    }

    [Fact]
    public async Task Administrator_SeesSystemWideAggregates()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var processIdA = await CreateProcessDefinitionAsync(setupDb);
        await PublishSingleUserTaskAsync(processIdA, userA);
        await StartAsync(processIdA, userA);

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(Guid.NewGuid(), new[] { AdministratorRole }, new AnalyticsQuery(ProcessDefinitionId: processIdA));

        Assert.Equal(1, overview.TotalProcesses);
    }

    // ---- Process Comparison ----

    [Fact]
    public async Task ProcessComparison_GroupsByDefinition()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(setupDb);
        var processA = await CreateProcessDefinitionAsync(setupDb, $"CmpA-{Guid.NewGuid():N}");
        var processB = await CreateProcessDefinitionAsync(setupDb, $"CmpB-{Guid.NewGuid():N}");
        await PublishSingleUserTaskAsync(processA, user);
        await PublishSingleUserTaskAsync(processB, user);
        await StartAsync(processA, user);
        await StartAsync(processA, user);
        await StartAsync(processB, user);

        await using var db = PostgresFixture.CreateContext();
        var overview = await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery());

        Assert.Equal(2, overview.ProcessComparison.Single(c => c.ProcessDefinitionId == processA).Total);
        Assert.Equal(1, overview.ProcessComparison.Single(c => c.ProcessDefinitionId == processB).Total);
    }

    // Phase 10 SHOULD HAVE #7 — regression proof that BuildNodeAnalyticsAsync's 2N+1 query
    // pattern (two extra DB round trips per distinct (ProcessDefinitionId, NodeId) group, in a
    // serial loop) is gone. A DbCommandInterceptor counts real SQL statements executed for
    // GetOverviewAsync, scoped to a single ProcessDefinition (so BuildProcessComparisonAsync's
    // own, separate, out-of-scope-for-this-fix per-process-definition loop can't confound the
    // measurement — it always sees exactly one process definition here). If the old per-node-group
    // loop still existed, a workflow with more distinct UserTask nodes would visibly add commands
    // (+2 per extra node). With the batched fix, the command count must not grow with node count.
    [Fact]
    public async Task NodeAnalytics_QueryCount_DoesNotScaleWithNumberOfDistinctNodeGroups()
    {
        var user = Guid.NewGuid();
        await using (var setupDb = PostgresFixture.CreateContext())
        {
            setupDb.Users.Add(new User { Id = user, Username = $"user-{Guid.NewGuid():N}", DisplayName = "Query Count User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true });
            await setupDb.SaveChangesAsync();
        }

        async Task<int> RunOverviewAndCountCommandsAsync(int nodeCount)
        {
            var nodes = new List<WorkflowNodeDefinition> { new("start", WorkflowNodeType.Start, "Start") };
            var transitions = new List<WorkflowTransitionDefinition>();
            var previousNodeId = "start";
            for (var i = 0; i < nodeCount; i++)
            {
                var nodeId = $"task{i}";
                nodes.Add(new WorkflowNodeDefinition(nodeId, WorkflowNodeType.UserTask, $"Task {i}", new WorkflowAssignment(WorkflowAssignmentType.User, user.ToString())));
                transitions.Add(new WorkflowTransitionDefinition($"t-{previousNodeId}-{nodeId}", previousNodeId, nodeId));
                previousNodeId = nodeId;
            }
            nodes.Add(new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"));
            transitions.Add(new WorkflowTransitionDefinition($"t-{previousNodeId}-end", previousNodeId, "end"));
            var graph = new WorkflowDefinition(nodes, transitions);

            await using var setupDb = PostgresFixture.CreateContext();
            var processId = await CreateProcessDefinitionAsync(setupDb);
            await NewDefinitionService(setupDb).CreateVersionAsync(processId, new CreateProcessVersionRequest(graph));
            await using (var publishDb = PostgresFixture.CreateContext())
            {
                await NewEngine(publishDb).PublishVersionAsync(processId, Guid.NewGuid());
            }

            var instanceId = await StartAsync(processId, user);
            for (var i = 0; i < nodeCount; i++)
            {
                await using var taskDb = PostgresFixture.CreateContext();
                var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == $"task{i}");
                await NewEngine(taskDb).CompleteTaskAsync(task.Id, user, Array.Empty<string>());
            }

            var counter = new CommandCountInterceptor();
            var options = new DbContextOptionsBuilder<BpmDbContext>()
                .UseNpgsql(PostgresFixture.TestConnectionString)
                .AddInterceptors(counter)
                .Options;
            await using var db = new BpmDbContext(options);
            await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery(ProcessDefinitionId: processId));

            return counter.CommandCount;
        }

        var countWithOneNode = await RunOverviewAndCountCommandsAsync(1);
        var countWithFiveNodes = await RunOverviewAndCountCommandsAsync(5);

        Assert.Equal(countWithOneNode, countWithFiveNodes);
    }

    // Phase 12 — regression proof that BuildProcessComparisonAsync's 2N+1 query pattern (two
    // extra DB round trips per distinct ProcessDefinitionId, in a serial loop) is gone. Confirmed
    // live against the real dev database before this fix: 987 distinct process definitions with
    // instances produced ~1,975 queries in one GetOverviewAsync call — the dominant cost of the
    // measured 1.1s /api/analytics/overview response. A DbCommandInterceptor counts real SQL
    // statements executed for GetOverviewAsync with NO ProcessDefinitionId filter (so
    // BuildProcessComparisonAsync genuinely sees multiple distinct process definitions, unlike
    // the NodeAnalytics test above which deliberately isolates to one). If the old per-definition
    // loop still existed, going from 1 to 5 distinct process definitions would visibly add
    // commands (+2 per extra definition). With the batched fix, the command count must not grow.
    [Fact]
    public async Task ProcessComparison_QueryCount_DoesNotScaleWithNumberOfDistinctProcessDefinitions()
    {
        var user = Guid.NewGuid();
        await using (var setupDb = PostgresFixture.CreateContext())
        {
            setupDb.Users.Add(new User { Id = user, Username = $"user-{Guid.NewGuid():N}", DisplayName = "Comparison Query Count User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true });
            await setupDb.SaveChangesAsync();
        }

        async Task<int> RunOverviewAndCountCommandsAsync(int definitionCount)
        {
            for (var i = 0; i < definitionCount; i++)
            {
                await using var setupDb = PostgresFixture.CreateContext();
                var processId = await CreateProcessDefinitionAsync(setupDb);
                await PublishSingleUserTaskAsync(processId, user);
                var instanceId = await StartAsync(processId, user);

                await using var taskDb = PostgresFixture.CreateContext();
                var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId);
                await NewEngine(taskDb).CompleteTaskAsync(task.Id, user, Array.Empty<string>());
            }

            var counter = new CommandCountInterceptor();
            var options = new DbContextOptionsBuilder<BpmDbContext>()
                .UseNpgsql(PostgresFixture.TestConnectionString)
                .AddInterceptors(counter)
                .Options;
            await using var db = new BpmDbContext(options);
            await NewAnalyticsService(db).GetOverviewAsync(user, Array.Empty<string>(), new AnalyticsQuery());

            return counter.CommandCount;
        }

        var countWithOneDefinition = await RunOverviewAndCountCommandsAsync(1);
        var countWithFiveMoreDefinitions = await RunOverviewAndCountCommandsAsync(5);

        Assert.Equal(countWithOneDefinition, countWithFiveMoreDefinitions);
    }

    private class CommandCountInterceptor : DbCommandInterceptor
    {
        public int CommandCount { get; private set; }

        public override InterceptionResult<System.Data.Common.DbDataReader> ReaderExecuting(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result)
        {
            CommandCount++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            CommandCount++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
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
