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

// Phase 6.1 — Notification Foundation, event-integration coverage: proves each trigger point
// (WorkflowTransitions.CreateTaskForNodeAsync, ApprovalEngine.CreateApprovalTaskAsync/ReturnAsync/
// ResolveApprovalOutcomeAsync, WorkflowEngine.CompleteTaskAsync) actually persists the right
// Notification, for the right recipient(s), atomically with the real business operation — against
// the same real Postgres database every other engine integration test uses, not a mock.
[Collection("Postgres")]
public class NotificationIntegrationTests
{
    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

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

    private static async Task<Guid> PublishAsync(WorkflowDefinition graph)
    {
        var key = $"notif-{Guid.NewGuid():N}";
        await using var setupDb = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(setupDb);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "Notification Test", null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());
        return definition.Id;
    }

    private static async Task<ProcessInstanceDto> StartAsync(Guid processDefinitionId, Guid initiatorId)
    {
        await using var keyDb = PostgresFixture.CreateContext();
        var key = (await keyDb.ProcessDefinitions.SingleAsync(p => p.Id == processDefinitionId)).Key;

        await using var startDb = PostgresFixture.CreateContext();
        return await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), initiatorId);
    }

    private static async Task<List<Notification>> NotificationsForAsync(Guid recipientUserId)
    {
        await using var db = PostgresFixture.CreateContext();
        return await db.Notifications.Where(n => n.RecipientUserId == recipientUserId).ToListAsync();
    }

    [Fact]
    public async Task TaskAssigned_DirectUserAssignment_NotifiesThatUser()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var assignee = await CreateUserAsync(setupDb);

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.User, assignee.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        var definitionId = await PublishAsync(graph);
        await StartAsync(definitionId, Guid.NewGuid());

        var notifications = await NotificationsForAsync(assignee);
        Assert.Contains(notifications, n => n.Type == NotificationType.TaskAssigned);
    }

    [Fact]
    public async Task TaskAssigned_RoleAssignment_NotifiesEveryRoleHolder()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, userA, userB);

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.Role, role)),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        var definitionId = await PublishAsync(graph);
        await StartAsync(definitionId, Guid.NewGuid());

        Assert.Contains((await NotificationsForAsync(userA)), n => n.Type == NotificationType.TaskAssigned);
        Assert.Contains((await NotificationsForAsync(userB)), n => n.Type == NotificationType.TaskAssigned);
    }

    [Fact]
    public async Task ApprovalRequired_AnyOnePolicy_NotifiesEveryCandidate()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, userA, userB);

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                ApprovalNode("approval", "Approval", ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, role) }),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "approval"), new WorkflowTransitionDefinition("t2", "approval", "end") });

        var definitionId = await PublishAsync(graph);
        await StartAsync(definitionId, Guid.NewGuid());

        Assert.Contains((await NotificationsForAsync(userA)), n => n.Type == NotificationType.ApprovalRequired);
        Assert.Contains((await NotificationsForAsync(userB)), n => n.Type == NotificationType.ApprovalRequired);
    }

    [Fact]
    public async Task ApprovalRequired_SequentialPolicy_OnlyNotifiesFirstCandidate()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, userA, userB);

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                ApprovalNode("approval", "Approval", ApprovalPolicy.Sequential, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, role) }),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "approval"), new WorkflowTransitionDefinition("t2", "approval", "end") });

        var definitionId = await PublishAsync(graph);
        var instance = await StartAsync(definitionId, Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var approval = await db.ApprovalInstances.Include(a => a.Assignments).SingleAsync(a => a.TaskInstance!.ProcessInstanceId == instance.Id);
        var firstOrderUserId = approval.Assignments.OrderBy(a => a.Order).First().UserId;
        var secondOrderUserId = approval.Assignments.OrderBy(a => a.Order).Last().UserId;

        Assert.Contains((await NotificationsForAsync(firstOrderUserId)), n => n.Type == NotificationType.ApprovalRequired);
        Assert.DoesNotContain((await NotificationsForAsync(secondOrderUserId)), n => n.Type == NotificationType.ApprovalRequired);
    }

    [Fact]
    public async Task Approve_AnyOne_NotifiesInitiator_ApprovalCompleted_ThenProcessCompleted()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var approver = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, approver);
        var initiator = Guid.NewGuid();

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                ApprovalNode("approval", "Approval", ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, role) }),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "approval"), new WorkflowTransitionDefinition("t2", "approval", "end") });

        var definitionId = await PublishAsync(graph);
        var instance = await StartAsync(definitionId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var taskId = (await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id)).Id;

        await using var approveDb = PostgresFixture.CreateContext();
        await NewEngine(approveDb).ApproveTaskAsync(taskId, approver, Array.Empty<string>());

        var initiatorNotifications = await NotificationsForAsync(initiator);
        Assert.Contains(initiatorNotifications, n => n.Type == NotificationType.ApprovalCompleted);
        Assert.Contains(initiatorNotifications, n => n.Type == NotificationType.ProcessCompleted);
    }

    [Fact]
    public async Task Reject_NotifiesInitiator_ProcessRejected_NotApprovalCompleted()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var approver = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, approver);
        var initiator = Guid.NewGuid();

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                ApprovalNode("approval", "Approval", ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, role) }),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "approval"), new WorkflowTransitionDefinition("t2", "approval", "end") });

        var definitionId = await PublishAsync(graph);
        var instance = await StartAsync(definitionId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var taskId = (await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id)).Id;

        await using var rejectDb = PostgresFixture.CreateContext();
        await NewEngine(rejectDb).RejectTaskAsync(taskId, approver, Array.Empty<string>());

        var initiatorNotifications = await NotificationsForAsync(initiator);
        Assert.Contains(initiatorNotifications, n => n.Type == NotificationType.ProcessRejected);
        Assert.DoesNotContain(initiatorNotifications, n => n.Type == NotificationType.ApprovalCompleted);
    }

    [Fact]
    public async Task Return_NotifiesOtherPendingApprovers_AndInitiator_DistinctTypes()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var actor = await CreateUserAsync(setupDb);
        var otherApprover = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, actor, otherApprover);
        var initiator = Guid.NewGuid();

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

        var definitionId = await PublishAsync(graph);
        var instance = await StartAsync(definitionId, initiator);

        await using var completeDb = PostgresFixture.CreateContext();
        var applicantTaskId = (await completeDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id)).Id;
        await NewEngine(completeDb).CompleteTaskAsync(applicantTaskId, initiator, Array.Empty<string>());

        await using var taskDb = PostgresFixture.CreateContext();
        var approvalTaskId = (await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id && t.NodeId == "approval")).Id;

        await using var returnDb = PostgresFixture.CreateContext();
        await NewEngine(returnDb).ReturnTaskAsync(approvalTaskId, actor, Array.Empty<string>());

        Assert.Contains((await NotificationsForAsync(otherApprover)), n => n.Type == NotificationType.ApprovalReturned);
        Assert.Contains((await NotificationsForAsync(initiator)), n => n.Type == NotificationType.ProcessReturned);
        // The returned-to person is the actor (co-approver), not the acting approver themselves —
        // and the acting approver never gets ApprovalReturned about their own action.
        Assert.DoesNotContain((await NotificationsForAsync(actor)), n => n.Type == NotificationType.ApprovalReturned);
    }

    [Fact]
    public async Task CompleteTask_NotifiesInitiator_UnlessInitiatorIsTheCompleter()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var completer = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, completer);
        var initiator = Guid.NewGuid();

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.Role, role)),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        var definitionId = await PublishAsync(graph);
        var instance = await StartAsync(definitionId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var taskId = (await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id)).Id;

        await using var completeDb = PostgresFixture.CreateContext();
        await NewEngine(completeDb).CompleteTaskAsync(taskId, completer, new[] { role });

        Assert.Contains((await NotificationsForAsync(initiator)), n => n.Type == NotificationType.TaskCompleted);
    }

    [Fact]
    public async Task CompleteTask_DuplicateCompleteAttempt_DoesNotCreateExtraNotification()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var completer = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, completer);
        var initiator = Guid.NewGuid();

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Review", new WorkflowAssignment(WorkflowAssignmentType.Role, role)),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "task"), new WorkflowTransitionDefinition("t2", "task", "end") });

        var definitionId = await PublishAsync(graph);
        var instance = await StartAsync(definitionId, initiator);

        await using var taskDb = PostgresFixture.CreateContext();
        var taskId = (await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id)).Id;

        await using var firstCompleteDb = PostgresFixture.CreateContext();
        await NewEngine(firstCompleteDb).CompleteTaskAsync(taskId, completer, new[] { role });

        // Retry (e.g. a duplicate client request) fails closed via the engine's existing
        // idempotency-by-state-check (TASK_ALREADY_COMPLETED) before reaching the notification
        // code at all — so no second TaskCompleted notification is created.
        await using var retryDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ConflictAppException>(() => NewEngine(retryDb).CompleteTaskAsync(taskId, completer, new[] { role }));

        var notifications = (await NotificationsForAsync(initiator)).Where(n => n.Type == NotificationType.TaskCompleted).ToList();
        Assert.Single(notifications);
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
