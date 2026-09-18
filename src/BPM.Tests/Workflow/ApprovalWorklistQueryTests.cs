using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Workflow;

// Phase 5.5.1 — Approvals Worklist. TaskQueryService.GetApprovalWorklistAsync is a new read-only
// query, not a new approval state machine — these tests cover the query's own authorization
// scoping, filtering, search, and pagination; Sequential/AnyOne/Reject/Return/Delegate/Transfer/
// AddApprover semantics themselves remain ApprovalEngineIntegrationTests' territory and are not
// re-tested here.
[Collection("Postgres")]
public class ApprovalWorklistQueryTests
{
    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);
    private static TaskQueryService NewQueryService(BpmDbContext db) => new(db);

    private static async Task<Guid> CreateUserAsync(BpmDbContext db)
    {
        var user = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = "Test User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<(ProcessDefinitionDto Definition, Guid TaskId, Guid ProcessInstanceId)> PublishAndStartSingleApprovalAsync(
        Guid initiatorId, ApprovalPolicy policy, IReadOnlyList<WorkflowAssignment> assignments, string processName, string? key = null)
    {
        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.ApprovalTask, "Approval Step", Approval: new ApprovalConfig(policy, assignments)),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "approval"),
                new WorkflowTransitionDefinition("t2", "approval", "end"),
            });

        key ??= $"worklist-{Guid.NewGuid():N}";

        await using var setupDb = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(setupDb);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, processName, null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), initiatorId);

        await using var taskDb = PostgresFixture.CreateContext();
        var taskId = (await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id)).Id;

        return (definition, taskId, instance.Id);
    }

    [Fact]
    public async Task GetApprovalWorklist_OnlyShowsTasksTheCallerIsAnApprovalParticipantOf_NoCrossUserLeakage()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var applicant = await CreateUserAsync(setupDb);
        var approverA = await CreateUserAsync(setupDb);
        var approverB = await CreateUserAsync(setupDb);
        var stranger = await CreateUserAsync(setupDb);

        await PublishAndStartSingleApprovalAsync(applicant, ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, approverA.ToString()) }, "For A");
        await PublishAndStartSingleApprovalAsync(applicant, ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, approverB.ToString()) }, "For B");

        await using var db = PostgresFixture.CreateContext();
        var service = NewQueryService(db);

        var aWorklist = await service.GetApprovalWorklistAsync(approverA, Array.Empty<string>(), new ApprovalWorklistQuery());
        var item = Assert.Single(aWorklist.Items);
        Assert.Equal("For A", item.ProcessDefinitionName);
        Assert.Equal(applicant, item.ApplicantId);

        var bWorklist = await service.GetApprovalWorklistAsync(approverB, Array.Empty<string>(), new ApprovalWorklistQuery());
        Assert.Single(bWorklist.Items);
        Assert.Equal("For B", bWorklist.Items[0].ProcessDefinitionName);

        var strangerWorklist = await service.GetApprovalWorklistAsync(stranger, Array.Empty<string>(), new ApprovalWorklistQuery());
        Assert.Empty(strangerWorklist.Items);
        Assert.Equal(0, strangerWorklist.TotalCount);
    }

    [Fact]
    public async Task GetApprovalWorklist_ExposesApprovalProgress_MatchingEngineSemantics()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var applicant = await CreateUserAsync(setupDb);
        var approverA = await CreateUserAsync(setupDb);
        var approverB = await CreateUserAsync(setupDb);
        var approverC = await CreateUserAsync(setupDb);

        var (_, taskId, _) = await PublishAndStartSingleApprovalAsync(
            applicant, ApprovalPolicy.AnyOne,
            new[]
            {
                new WorkflowAssignment(WorkflowAssignmentType.User, approverA.ToString()),
                new WorkflowAssignment(WorkflowAssignmentType.User, approverB.ToString()),
                new WorkflowAssignment(WorkflowAssignmentType.User, approverC.ToString()),
            },
            "AnyOne Progress");

        await using var db = PostgresFixture.CreateContext();
        var worklist = await NewQueryService(db).GetApprovalWorklistAsync(approverA, Array.Empty<string>(), new ApprovalWorklistQuery());
        var item = Assert.Single(worklist.Items);
        Assert.Equal(taskId, item.TaskId);
        Assert.NotNull(item.Approval);
        Assert.Equal(ApprovalPolicy.AnyOne, item.Approval!.Policy);
        Assert.Equal(1, item.Approval.RequiredCount); // AnyOne: 1 required regardless of candidate count.
        Assert.Equal(0, item.Approval.ApprovedCount);
        Assert.Equal(3, item.Approval.Assignments.Count);
    }

    [Fact]
    public async Task GetApprovalWorklist_StatusFilter_MovesFromPendingToCompletedAfterApproval()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var applicant = await CreateUserAsync(setupDb);
        var approver = await CreateUserAsync(setupDb);

        var (_, taskId, _) = await PublishAndStartSingleApprovalAsync(applicant, ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, approver.ToString()) }, "Status Filter Test");

        await using var pendingDb = PostgresFixture.CreateContext();
        var pending = await NewQueryService(pendingDb).GetApprovalWorklistAsync(approver, Array.Empty<string>(), new ApprovalWorklistQuery(Status: TaskInstanceStatus.Pending));
        Assert.Single(pending.Items);

        await using var completedBeforeDb = PostgresFixture.CreateContext();
        var completedBefore = await NewQueryService(completedBeforeDb).GetApprovalWorklistAsync(approver, Array.Empty<string>(), new ApprovalWorklistQuery(Status: TaskInstanceStatus.Completed));
        Assert.Empty(completedBefore.Items);

        await using var approveDb = PostgresFixture.CreateContext();
        await NewEngine(approveDb).ApproveTaskAsync(taskId, approver, Array.Empty<string>());

        await using var pendingAfterDb = PostgresFixture.CreateContext();
        var pendingAfter = await NewQueryService(pendingAfterDb).GetApprovalWorklistAsync(approver, Array.Empty<string>(), new ApprovalWorklistQuery(Status: TaskInstanceStatus.Pending));
        Assert.Empty(pendingAfter.Items);

        await using var completedAfterDb = PostgresFixture.CreateContext();
        var completedAfter = await NewQueryService(completedAfterDb).GetApprovalWorklistAsync(approver, Array.Empty<string>(), new ApprovalWorklistQuery(Status: TaskInstanceStatus.Completed));
        Assert.Single(completedAfter.Items);

        // "All" (no status filter) still shows it, exactly once.
        await using var allDb = PostgresFixture.CreateContext();
        var all = await NewQueryService(allDb).GetApprovalWorklistAsync(approver, Array.Empty<string>(), new ApprovalWorklistQuery());
        Assert.Single(all.Items);
    }

    [Fact]
    public async Task GetApprovalWorklist_SearchMatchesProcessNameOrKey()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var applicant = await CreateUserAsync(setupDb);
        var approver = await CreateUserAsync(setupDb);

        await PublishAndStartSingleApprovalAsync(applicant, ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, approver.ToString()) }, "Expense Request Alpha");
        await PublishAndStartSingleApprovalAsync(applicant, ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, approver.ToString()) }, "Purchase Order Beta");

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetApprovalWorklistAsync(approver, Array.Empty<string>(), new ApprovalWorklistQuery(Search: "Expense"));

        var item = Assert.Single(result.Items);
        Assert.Equal("Expense Request Alpha", item.ProcessDefinitionName);
    }

    [Fact]
    public async Task GetApprovalWorklist_Paginates_WithCorrectTotalCount()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var applicant = await CreateUserAsync(setupDb);
        var approver = await CreateUserAsync(setupDb);

        for (var i = 0; i < 3; i++)
        {
            await PublishAndStartSingleApprovalAsync(applicant, ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, approver.ToString()) }, $"Paged Process {i}");
        }

        await using var page1Db = PostgresFixture.CreateContext();
        var page1 = await NewQueryService(page1Db).GetApprovalWorklistAsync(approver, Array.Empty<string>(), new ApprovalWorklistQuery(Page: 1, PageSize: 2));
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(1, page1.Page);

        await using var page2Db = PostgresFixture.CreateContext();
        var page2 = await NewQueryService(page2Db).GetApprovalWorklistAsync(approver, Array.Empty<string>(), new ApprovalWorklistQuery(Page: 2, PageSize: 2));
        Assert.Single(page2.Items);
        Assert.Equal(3, page2.TotalCount);

        // No overlap between pages.
        Assert.DoesNotContain(page2.Items[0].TaskId, page1.Items.Select(i => i.TaskId));
    }

    [Fact]
    public async Task GetApprovalWorklist_InvalidPageOrPageSize_FallsBackToDefaults()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var applicant = await CreateUserAsync(setupDb);
        var approver = await CreateUserAsync(setupDb);
        await PublishAndStartSingleApprovalAsync(applicant, ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, approver.ToString()) }, "Defaults Test");

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetApprovalWorklistAsync(approver, Array.Empty<string>(), new ApprovalWorklistQuery(Page: -1, PageSize: 999));

        Assert.Equal(1, result.Page);
        Assert.Equal(20, result.PageSize);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task GetApprovalWorklist_DelegatedApprover_SeesTheDelegatedTask()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var applicant = await CreateUserAsync(setupDb);
        var original = await CreateUserAsync(setupDb);
        var delegateUser = await CreateUserAsync(setupDb);

        var (_, taskId, _) = await PublishAndStartSingleApprovalAsync(applicant, ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, original.ToString()) }, "Delegate Worklist Test");

        await using var delegateDb = PostgresFixture.CreateContext();
        await NewEngine(delegateDb).DelegateTaskAsync(taskId, original, delegateUser, CancellationToken.None);

        await using var db = PostgresFixture.CreateContext();
        var delegateWorklist = await NewQueryService(db).GetApprovalWorklistAsync(delegateUser, Array.Empty<string>(), new ApprovalWorklistQuery());
        Assert.Single(delegateWorklist.Items);

        // Delegate is additive — the original owner can still see (and act on) it too.
        var originalWorklist = await NewQueryService(db).GetApprovalWorklistAsync(original, Array.Empty<string>(), new ApprovalWorklistQuery());
        Assert.Single(originalWorklist.Items);
    }

    // Phase 12 — same overflow guard Phase 7.2.3 established in ProcessMonitoringQueryService,
    // now applied here too (see TaskQueryService.cs's own comment on GetApprovalWorklistAsync).
    [Fact]
    public async Task GetApprovalWorklist_ExtremelyLargePage_DoesNotThrow_ReturnsEmptyResult()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var applicant = await CreateUserAsync(setupDb);
        var approver = await CreateUserAsync(setupDb);
        await PublishAndStartSingleApprovalAsync(applicant, ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, approver.ToString()) }, "Extreme Page Test");

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetApprovalWorklistAsync(approver, Array.Empty<string>(), new ApprovalWorklistQuery(Page: int.MaxValue, PageSize: 200));

        Assert.Empty(result.Items);
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
