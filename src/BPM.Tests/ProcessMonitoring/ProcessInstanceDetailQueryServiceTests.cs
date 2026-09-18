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

// Phase 7.2.2 — Process Instance Detail + Timeline. Setup helpers mirror
// ProcessMonitoringQueryServiceTests.cs's own style (self-contained per file).
[Collection("Postgres")]
public class ProcessInstanceDetailQueryServiceTests
{
    private const string AdministratorRole = "Administrator";

    private static ProcessInstanceDetailQueryService NewDetailService(BpmDbContext db) =>
        new(db, new ProcessInstanceQueryService(db));

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

    private static async Task<Guid> CreateProcessDefinitionAsync(BpmDbContext db, string? name = null) =>
        (await NewDefinitionService(db).CreateAsync(new CreateProcessDefinitionRequest($"detail-{Guid.NewGuid():N}", name ?? "Detail Test", null, null))).Id;

    private static async Task PublishTwoUserTasksAsync(Guid processDefinitionId, Guid firstAssignee, Guid secondAssignee)
    {
        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("first", WorkflowNodeType.UserTask, "First Step", new WorkflowAssignment(WorkflowAssignmentType.User, firstAssignee.ToString())),
                new WorkflowNodeDefinition("second", WorkflowNodeType.UserTask, "Second Step", new WorkflowAssignment(WorkflowAssignmentType.User, secondAssignee.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "first"),
                new WorkflowTransitionDefinition("t2", "first", "second"),
                new WorkflowTransitionDefinition("t3", "second", "end"),
            });

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

    // ---- Process summary / authorization ----

    [Fact]
    public async Task AuthorizedUser_GetsProcessSummary()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb, "Amy Initiator");
        var other = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb, "Purchase Request");
        await PublishTwoUserTasksAsync(processId, initiator, other);
        var instanceId = await StartAsync(processId, initiator);

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        Assert.NotNull(detail);
        Assert.Equal(instanceId, detail!.ProcessInstanceId);
        Assert.Equal("Purchase Request", detail.ProcessDefinitionName);
        Assert.Equal(ProcessInstanceStatus.Running, detail.Status);
        Assert.Equal(initiator, detail.InitiatorId);
        Assert.Equal("Amy Initiator", detail.InitiatorDisplayName);
        Assert.Null(detail.CompletedAt);
    }

    [Fact]
    public async Task NonexistentProcess_ReturnsNull()
    {
        await using var db = PostgresFixture.CreateContext();
        var user = await CreateUserAsync(db);

        var detail = await NewDetailService(db).GetDetailAsync(Guid.NewGuid(), user, Array.Empty<string>());

        Assert.Null(detail);
    }

    [Fact]
    public async Task UnauthorizedUser_ThrowsForbidden_Idor()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var stranger = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var db = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ForbiddenAppException>(() => NewDetailService(db).GetDetailAsync(instanceId, stranger, Array.Empty<string>()));
    }

    [Fact]
    public async Task Administrator_CanAccessAnyProcess()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, Guid.NewGuid(), new[] { AdministratorRole });

        Assert.NotNull(detail);
        Assert.Equal(instanceId, detail!.ProcessInstanceId);
    }

    // ---- Current task ----

    [Fact]
    public async Task CurrentTask_ReflectsActiveTaskInstance()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var second = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishTwoUserTasksAsync(processId, initiator, second);
        var instanceId = await StartAsync(processId, initiator);

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        Assert.Equal(1, detail!.ActiveTaskCount);
        Assert.NotNull(detail.CurrentTask);
        Assert.Equal("First Step", detail.CurrentTask!.NodeName);
    }

    [Fact]
    public async Task CompletedProcess_HasNoCurrentTask()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var firstTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == "first");
        await NewEngine(taskDb).CompleteTaskAsync(firstTask.Id, initiator, Array.Empty<string>());
        await using var taskDb2 = PostgresFixture.CreateContext();
        var secondTask = await taskDb2.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == "second");
        await NewEngine(taskDb2).CompleteTaskAsync(secondTask.Id, initiator, Array.Empty<string>());

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        Assert.Equal(ProcessInstanceStatus.Completed, detail!.Status);
        Assert.NotNull(detail.CompletedAt);
        Assert.Equal(0, detail.ActiveTaskCount);
        Assert.Null(detail.CurrentTask);
    }

    // ---- Task history ----

    [Fact]
    public async Task TaskHistory_ContainsAllTasksInOrder()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var firstTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == "first");
        await NewEngine(taskDb).CompleteTaskAsync(firstTask.Id, initiator, Array.Empty<string>());

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        Assert.Equal(2, detail!.Tasks.Count);
        Assert.Equal("first", detail.Tasks[0].NodeId);
        Assert.Equal(TaskInstanceStatus.Completed, detail.Tasks[0].Status);
        Assert.Equal("second", detail.Tasks[1].NodeId);
        Assert.Equal(TaskInstanceStatus.Pending, detail.Tasks[1].Status);
    }

    // ---- Approval (reuses existing TaskDto.Approval — no separate approval history DTO) ----

    [Fact]
    public async Task ApprovalTask_ApprovalDataIsPopulated()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var approver = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, approver);
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishApprovalTaskAsync(processId, ApprovalPolicy.AnyOne, role);
        var instanceId = await StartAsync(processId, initiator);

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        var approvalTask = detail!.Tasks.Single(t => t.NodeId == "approval");
        Assert.NotNull(approvalTask.Approval);
        Assert.Equal(ApprovalPolicy.AnyOne, approvalTask.Approval!.Policy);
        Assert.Single(approvalTask.Approval.Assignments);
        Assert.Equal(approver, approvalTask.Approval.Assignments[0].UserId);
    }

    // ---- SLA ----

    [Fact]
    public async Task SlaSummary_ReflectsCurrentTasksAuthoritativeState()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "first", true, 60, 30));
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var firstTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == "first");
        var sla = await taskDb.TaskSlas.SingleAsync(s => s.TaskInstanceId == firstTask.Id);

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        Assert.NotNull(detail!.SlaSummary);
        Assert.Equal(TaskSlaStatus.Active, detail.SlaSummary!.Status);
        Assert.Equal(sla.DueAt, detail.SlaSummary.DueAt);

        // Historical data is not recalculated from current policy (Scenario L).
        await using var updateDb = PostgresFixture.CreateContext();
        var policy = await updateDb.SlaPolicies.SingleAsync(p => p.NodeId == "first" && p.ProcessDefinitionId == processId);
        await NewSlaService(updateDb).UpdateAsync(policy.Id, new UpdateSlaPolicyRequest(true, 999, 500, Convert.ToBase64String(policy.RowVersion)));

        await using var db2 = PostgresFixture.CreateContext();
        var detailAfter = await NewDetailService(db2).GetDetailAsync(instanceId, initiator, Array.Empty<string>());
        Assert.Equal(sla.DueAt, detailAfter!.SlaSummary!.DueAt);
    }

    [Fact]
    public async Task SlaSummary_TracksSchedulerOverdueTransition()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "first", true, 60, 30));
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var firstTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == "first");
        var sla = await taskDb.TaskSlas.SingleAsync(s => s.TaskInstanceId == firstTask.Id);

        await using (var db = PostgresFixture.CreateContext())
        {
            await NewSlaProcessor(db, new FakeClock { UtcNow = sla.DueAt }).ProcessBatchAsync();
        }

        await using var verifyDb = PostgresFixture.CreateContext();
        var detail = await NewDetailService(verifyDb).GetDetailAsync(instanceId, initiator, Array.Empty<string>());
        Assert.Equal(TaskSlaStatus.Overdue, detail!.SlaSummary!.Status);
    }

    // Phase 7.2.3 hardening regression test — a genuine bug: `ProcessInstanceDetailDto.SlaSummary`
    // (the existing, Phase 6.3 `TaskSlaDto`) has no `WarningNotifiedAt` field, so before this fix
    // Process Detail could never show `Warning` even when a task's SLA warning had genuinely
    // fired — while the same task, shown in `ProcessMonitoringQueryService`'s list, correctly
    // showed `Warning`. `ProcessInstanceDetailDto.SlaStatus` (new) now uses the exact same
    // `ProcessMonitoringSlaStatusMapper` both query services share, so the two views can never
    // disagree for the same TaskSla.
    [Fact]
    public async Task SlaStatus_AgreesWithProcessMonitoringList_ForTheSameTaskSla()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "first", true, 60, 30));
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var firstTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == "first");
        var sla = await taskDb.TaskSlas.SingleAsync(s => s.TaskInstanceId == firstTask.Id);

        await using (var db = PostgresFixture.CreateContext())
        {
            await NewSlaProcessor(db, new FakeClock { UtcNow = sla.WarningAt }).ProcessBatchAsync();
        }

        await using var monitoringDb = PostgresFixture.CreateContext();
        var monitoring = await new ProcessMonitoringQueryService(monitoringDb)
            .GetAsync(initiator, Array.Empty<string>(), new ProcessMonitoringQuery(ProcessDefinitionId: processId));
        var monitoringItem = monitoring.Items.Single(i => i.ProcessInstanceId == instanceId);

        await using var detailDb = PostgresFixture.CreateContext();
        var detail = await NewDetailService(detailDb).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        Assert.Equal(ProcessMonitoringSlaStatus.Warning, monitoringItem.SlaStatus);
        Assert.Equal(monitoringItem.SlaStatus, detail!.SlaStatus);
    }

    // ---- Workflow progress ----

    [Fact]
    public async Task WorkflowProgress_MarksCompletedCurrentAndPendingCorrectly()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var firstTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == "first");
        await NewEngine(taskDb).CompleteTaskAsync(firstTask.Id, initiator, Array.Empty<string>());

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        var progress = detail!.WorkflowProgress.ToDictionary(p => p.NodeId, p => p.State);
        Assert.Equal(ProcessProgressState.Completed, progress["start"]);
        Assert.Equal(ProcessProgressState.Completed, progress["first"]);
        Assert.Equal(ProcessProgressState.Current, progress["second"]);
        Assert.Equal(ProcessProgressState.Pending, progress["end"]);
    }

    [Fact]
    public async Task WorkflowProgress_UsesHistoricalProcessVersion_NotCurrentDraft()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        // Publish a second version with a different graph shape after the instance already
        // started — Process Detail must keep reflecting the original 4-node graph, never the new
        // definition (Part 25/26).
        var thirdUser = await CreateUserAsync(setupDb);
        await PublishTwoUserTasksAsync(processId, thirdUser, thirdUser);

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        // Still resolves against the original version's own "first" node, assigned to `initiator`
        // (not `thirdUser`, who only exists in the newer version).
        Assert.Equal("First Step", detail!.CurrentTask!.NodeName);
        Assert.Equal(initiator, detail.CurrentTask.AssigneeId);
    }

    [Fact]
    public async Task RejectedProcess_EndNodeNeverFalselyMarkedCompleted()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var approver = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, approver);
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishApprovalTaskAsync(processId, ApprovalPolicy.AnyOne, role);
        var instanceId = await StartAsync(processId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var approvalTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId);
        await NewEngine(taskDb).RejectTaskAsync(approvalTask.Id, approver, new[] { role });

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        Assert.Equal(ProcessInstanceStatus.Rejected, detail!.Status);
        var progress = detail.WorkflowProgress.ToDictionary(p => p.NodeId, p => p.State);
        Assert.Equal(ProcessProgressState.Completed, progress["approval"]); // execution passed through it.
        Assert.Equal(ProcessProgressState.Pending, progress["end"]); // never actually reached.
    }

    // ---- Timeline ----

    [Fact]
    public async Task Timeline_ContainsExpectedEvents_NewestFirst()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var firstTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == "first");
        await NewEngine(taskDb).CompleteTaskAsync(firstTask.Id, initiator, Array.Empty<string>());

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        var eventTypes = detail!.Timeline.Select(t => t.EventType).ToList();
        Assert.Contains(AuditActions.StartProcess, eventTypes);
        Assert.Contains(AuditActions.TaskCompleted, eventTypes);
        Assert.Contains(AuditActions.TaskCreated, eventTypes);

        // Newest first: the timeline's first item's timestamp must be >= every other item's.
        for (var i = 1; i < detail.Timeline.Count; i++)
        {
            Assert.True(detail.Timeline[i - 1].Timestamp >= detail.Timeline[i].Timestamp);
        }
    }

    [Fact]
    public async Task Timeline_ExcludesWorkflowTransitionNoise()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var firstTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == "first");
        await NewEngine(taskDb).CompleteTaskAsync(firstTask.Id, initiator, Array.Empty<string>());

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        Assert.DoesNotContain(detail!.Timeline, t => t.EventType == AuditActions.WorkflowTransition);
    }

    [Fact]
    public async Task Timeline_IncludesSlaEvents_FromPersistedTaskSlaFields()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "first", true, 60, 30));
        await PublishTwoUserTasksAsync(processId, initiator, initiator);
        var instanceId = await StartAsync(processId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var firstTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceId && t.NodeId == "first");
        var sla = await taskDb.TaskSlas.SingleAsync(s => s.TaskInstanceId == firstTask.Id);

        await using (var db = PostgresFixture.CreateContext())
        {
            await NewSlaProcessor(db, new FakeClock { UtcNow = sla.WarningAt }).ProcessBatchAsync();
        }

        await using var verifyDb = PostgresFixture.CreateContext();
        var detail = await NewDetailService(verifyDb).GetDetailAsync(instanceId, initiator, Array.Empty<string>());
        Assert.Contains(detail!.Timeline, t => t.EventType == "SlaWarning");
    }

    [Fact]
    public async Task Timeline_IsBounded_DoesNotThrowOnManyEvents()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var approver = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, approver);
        var initiator = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await PublishApprovalTaskAsync(processId, ApprovalPolicy.AnyOne, role);
        var instanceId = await StartAsync(processId, initiator);

        await using var db = PostgresFixture.CreateContext();
        var detail = await NewDetailService(db).GetDetailAsync(instanceId, initiator, Array.Empty<string>());

        Assert.True(detail!.Timeline.Count <= 200);
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
