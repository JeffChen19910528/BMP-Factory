using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Sla;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Workflow;

// Phase 6.3 — SLA Foundation, event-integration coverage: proves SLA creation/completion actually
// happens at the real trigger points (WorkflowTransitions.CreateTaskForNodeAsync,
// WorkflowEngine.CompleteTaskAsync, ApprovalEngine's outcome/Return paths) against the same real
// Postgres database every other engine integration test uses.
[Collection("Postgres")]
public class SlaIntegrationTests
{
    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static SlaPolicyService NewSlaService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new CreateSlaPolicyRequestValidator(), new UpdateSlaPolicyRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);

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

    private static WorkflowNodeDefinition ApprovalNode(string id, string name, ApprovalPolicy policy, IReadOnlyList<WorkflowAssignment> assignments) =>
        new(id, WorkflowNodeType.ApprovalTask, name, Approval: new ApprovalConfig(policy, assignments, true, true, true, true, true, new ReturnPolicy(true)));

    private static async Task<Guid> PublishAsync(WorkflowDefinition graph, Guid? processDefinitionId = null)
    {
        Guid definitionId;
        if (processDefinitionId is Guid existing)
        {
            definitionId = existing;
        }
        else
        {
            var key = $"sla-{Guid.NewGuid():N}";
            await using var setupDb = PostgresFixture.CreateContext();
            var definitionService = NewDefinitionService(setupDb);
            var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "SLA Test", null, null));
            definitionId = definition.Id;
        }

        await using var versionDb = PostgresFixture.CreateContext();
        var definitionService2 = NewDefinitionService(versionDb);
        await definitionService2.CreateVersionAsync(definitionId, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(definitionId, Guid.NewGuid());
        return definitionId;
    }

    private static async Task<Guid> CreateProcessDefinitionOnlyAsync(BpmDbContext db)
    {
        var definition = await NewDefinitionService(db).CreateAsync(new CreateProcessDefinitionRequest($"sla-{Guid.NewGuid():N}", "SLA Test", null, null));
        return definition.Id;
    }

    private static async Task<ProcessInstanceDto> StartAsync(Guid processDefinitionId, Guid initiatorId)
    {
        await using var keyDb = PostgresFixture.CreateContext();
        var key = (await keyDb.ProcessDefinitions.SingleAsync(p => p.Id == processDefinitionId)).Key;

        await using var startDb = PostgresFixture.CreateContext();
        return await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), initiatorId);
    }

    private static async Task<TaskSla?> GetSlaAsync(Guid taskInstanceId)
    {
        await using var db = PostgresFixture.CreateContext();
        return await db.TaskSlas.SingleOrDefaultAsync(s => s.TaskInstanceId == taskInstanceId);
    }

    [Fact]
    public async Task TaskCreation_WithApplicablePolicy_CreatesTaskSla_UserTask()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 1440, 720));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.User, assignee.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        await PublishAsync(graph, processId);
        var instance = await StartAsync(processId, Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var task = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);
        var sla = await GetSlaAsync(task.Id);

        Assert.NotNull(sla);
        Assert.Equal(TaskSlaStatus.Active, sla!.Status);
        Assert.Equal(sla.DueAt, task.DueAt);
    }

    [Fact]
    public async Task TaskCreation_WithApplicablePolicy_CreatesTaskSla_ApprovalTask()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var approver = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, approver);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 480, 60));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                ApprovalNode("approval", "Approval", ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, role) }),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "approval"), new WorkflowTransitionDefinition("t2", "approval", "end") });

        await PublishAsync(graph, processId);
        var instance = await StartAsync(processId, Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var task = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);
        var sla = await GetSlaAsync(task.Id);

        Assert.NotNull(sla);
        Assert.Equal(TaskSlaStatus.Active, sla!.Status);
    }

    [Fact]
    public async Task TaskCreation_NoApplicablePolicy_CreatesNoTaskSla()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        // No policy created for this process/node.

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.User, assignee.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        await PublishAsync(graph, processId);
        var instance = await StartAsync(processId, Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var task = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);
        Assert.Null(await GetSlaAsync(task.Id));
        Assert.Null(task.DueAt);
    }

    [Fact]
    public async Task TaskCreation_DisabledPolicy_CreatesNoTaskSla()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", false, 60, 30));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.User, assignee.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        await PublishAsync(graph, processId);
        var instance = await StartAsync(processId, Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var task = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);
        Assert.Null(await GetSlaAsync(task.Id));
    }

    [Fact]
    public async Task CompleteTask_MarksTaskSlaCompleted_PreservingOriginalDueAt()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, assignee);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.Role, role)),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        await PublishAsync(graph, processId);
        var instance = await StartAsync(processId, Guid.NewGuid());

        await using var taskDb = PostgresFixture.CreateContext();
        var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);
        var originalDueAt = (await GetSlaAsync(task.Id))!.DueAt;

        await using var completeDb = PostgresFixture.CreateContext();
        await NewEngine(completeDb).CompleteTaskAsync(task.Id, assignee, new[] { role });

        var sla = await GetSlaAsync(task.Id);
        Assert.Equal(TaskSlaStatus.Completed, sla!.Status);
        Assert.NotNull(sla.CompletedAt);
        Assert.Equal(originalDueAt, sla.DueAt);
    }

    [Fact]
    public async Task ApprovalReject_MarksTaskSlaCompleted_NotCancelled()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var approver = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, approver);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 60, 30));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                ApprovalNode("approval", "Approval", ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, role) }),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "approval"), new WorkflowTransitionDefinition("t2", "approval", "end") });

        await PublishAsync(graph, processId);
        var instance = await StartAsync(processId, Guid.NewGuid());

        await using var taskDb = PostgresFixture.CreateContext();
        var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);

        await using var rejectDb = PostgresFixture.CreateContext();
        await NewEngine(rejectDb).RejectTaskAsync(task.Id, approver, Array.Empty<string>());

        var sla = await GetSlaAsync(task.Id);
        Assert.Equal(TaskSlaStatus.Completed, sla!.Status);
    }

    [Fact]
    public async Task ApprovalReturn_OldTaskSlaCompleted_NewTaskGetsFreshSla()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var actor = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, actor);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "applicant", true, 60, 30));
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "approval", true, 120, 60));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("applicant", WorkflowNodeType.UserTask, "Applicant", new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, "")),
                ApprovalNode("approval", "Approval", ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, role) }),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "applicant"),
                new WorkflowTransitionDefinition("t2", "applicant", "approval"),
                new WorkflowTransitionDefinition("t3", "approval", "end"),
            });

        await PublishAsync(graph, processId);
        var initiator = Guid.NewGuid();
        var instance = await StartAsync(processId, initiator);

        await using var completeDb = PostgresFixture.CreateContext();
        var applicantTask = await completeDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);
        await NewEngine(completeDb).CompleteTaskAsync(applicantTask.Id, initiator, Array.Empty<string>());

        await using var taskDb = PostgresFixture.CreateContext();
        var approvalTask = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id && t.NodeId == "approval");

        await using var returnDb = PostgresFixture.CreateContext();
        await NewEngine(returnDb).ReturnTaskAsync(approvalTask.Id, actor, Array.Empty<string>());

        var oldSla = await GetSlaAsync(approvalTask.Id);
        Assert.Equal(TaskSlaStatus.Completed, oldSla!.Status);

        await using var verifyDb = PostgresFixture.CreateContext();
        var newApplicantTask = await verifyDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id && t.NodeId == "applicant" && t.Id != applicantTask.Id);
        var newSla = await GetSlaAsync(newApplicantTask.Id);
        Assert.NotNull(newSla);
        Assert.Equal(TaskSlaStatus.Active, newSla!.Status);
        Assert.NotEqual(oldSla.Id, newSla.Id);
    }

    [Fact]
    public async Task MultipleIndependentTasks_EachReceiveOwnTaskSla()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, "")),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        await PublishAsync(graph, processId);
        var instanceA = await StartAsync(processId, userA);
        var instanceB = await StartAsync(processId, userB);

        await using var db = PostgresFixture.CreateContext();
        var taskA = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceA.Id);
        var taskB = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceB.Id);

        var slaA = await GetSlaAsync(taskA.Id);
        var slaB = await GetSlaAsync(taskB.Id);
        Assert.NotNull(slaA);
        Assert.NotNull(slaB);
        Assert.NotEqual(slaA!.Id, slaB!.Id);
    }

    [Fact]
    public async Task PolicyChange_DoesNotRecalculateExistingTaskSla_NewTasksUseUpdatedPolicy()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var initiatorA = Guid.NewGuid();
        var initiatorB = Guid.NewGuid();
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        var policy = await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, "")),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });
        await PublishAsync(graph, processId);

        var instanceA = await StartAsync(processId, initiatorA);
        await using var taskDbA = PostgresFixture.CreateContext();
        var taskA = await taskDbA.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceA.Id);
        var slaABefore = await GetSlaAsync(taskA.Id);

        // Change the policy's duration from 60 to 180 minutes.
        await using var updateDb = PostgresFixture.CreateContext();
        await NewSlaService(updateDb).UpdateAsync(policy.Id, new UpdateSlaPolicyRequest(true, 180, 60, policy.RowVersion));

        // Task A's already-calculated SLA must be untouched by the policy edit.
        var slaAAfter = await GetSlaAsync(taskA.Id);
        Assert.Equal(slaABefore!.DueAt, slaAAfter!.DueAt);

        // A new task started after the policy change gets the new duration.
        var instanceB = await StartAsync(processId, initiatorB);
        await using var taskDbB = PostgresFixture.CreateContext();
        var taskB = await taskDbB.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instanceB.Id);
        var slaB = await GetSlaAsync(taskB.Id);

        var expectedDurationMinutesB = (slaB!.DueAt - slaB.StartedAt).TotalMinutes;
        Assert.Equal(180, expectedDurationMinutesB, 1);
    }

    [Fact]
    public async Task DuplicateTaskCompletion_DoesNotDoubleCompleteSla()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, assignee);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.Role, role)),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        await PublishAsync(graph, processId);
        var instance = await StartAsync(processId, Guid.NewGuid());

        await using var taskDb = PostgresFixture.CreateContext();
        var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);

        await using var firstCompleteDb = PostgresFixture.CreateContext();
        await NewEngine(firstCompleteDb).CompleteTaskAsync(task.Id, assignee, new[] { role });
        var completedAtFirst = (await GetSlaAsync(task.Id))!.CompletedAt;

        // Retry fails closed via the engine's existing idempotency-by-state-check before
        // SlaEngine.CompleteIfActiveAsync is even reached again.
        await using var retryDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ConflictAppException>(() => NewEngine(retryDb).CompleteTaskAsync(task.Id, assignee, new[] { role }));

        var slaAfterRetry = await GetSlaAsync(task.Id);
        Assert.Equal(TaskSlaStatus.Completed, slaAfterRetry!.Status);
        Assert.Equal(completedAtFirst, slaAfterRetry.CompletedAt);
    }

    [Fact]
    public async Task TaskDto_IncludesSla_ViaExistingAuthorizedTaskQuery_UnauthorizedUserStillRejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);
        var processId = await CreateProcessDefinitionOnlyAsync(setupDb);
        await NewSlaService(setupDb).CreateAsync(new CreateSlaPolicyRequest(processId, "task", true, 60, 30));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.User, assignee.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        await PublishAsync(graph, processId);
        var instance = await StartAsync(processId, Guid.NewGuid());

        await using var taskIdDb = PostgresFixture.CreateContext();
        var taskId = (await taskIdDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id)).Id;

        await using var db = PostgresFixture.CreateContext();
        var queryService = new TaskQueryService(db);
        var dto = await queryService.GetByIdAsync(taskId, assignee, Array.Empty<string>());
        Assert.NotNull(dto!.Sla);
        Assert.Equal(TaskSlaStatus.Active, dto.Sla!.Status);

        // An unrelated user gets the existing, unchanged Forbidden — SLA data rides along on the
        // already-authorized DTO, so it's never reachable independently of that check.
        await using var db2 = PostgresFixture.CreateContext();
        var unrelatedUser = await CreateUserAsync(db2);
        var queryService2 = new TaskQueryService(db2);
        await Assert.ThrowsAsync<ForbiddenAppException>(() => queryService2.GetByIdAsync(taskId, unrelatedUser, Array.Empty<string>()));
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
