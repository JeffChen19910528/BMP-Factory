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

// Phase 3 Approval Engine coverage, against the same real "bpm_test" Postgres database Phase 2's
// WorkflowEngineIntegrationTests uses (see PostgresFixture) — concurrency and transactional
// behavior are exactly the kind of thing a mock can't honestly exercise (Skill.md Phase 3 §31).
[Collection("Postgres")]
public class ApprovalEngineIntegrationTests
{
    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);

    private static async Task<Guid> CreateUserAsync(BpmDbContext db, string? username = null)
    {
        var user = new User
        {
            Username = username ?? $"user-{Guid.NewGuid():N}",
            DisplayName = "Test User",
            Email = $"{Guid.NewGuid():N}@bpm-tests.local",
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<string> CreateRoleWithMembersAsync(BpmDbContext db, params Guid[] userIds)
    {
        var roleName = $"Role-{Guid.NewGuid():N}";
        var role = new Role { Name = roleName };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        foreach (var userId in userIds)
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
        }
        await db.SaveChangesAsync();

        return roleName;
    }

    private static async Task<Guid> CreateDepartmentAsync(BpmDbContext db, Guid? managerUserId, params Guid[] memberUserIds)
    {
        var org = new Organization { Name = $"Org-{Guid.NewGuid():N}" };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        var department = new Department { Name = $"Dept-{Guid.NewGuid():N}", OrganizationId = org.Id, ManagerUserId = managerUserId };
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        foreach (var userId in memberUserIds)
        {
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.DepartmentId = department.Id;
        }
        await db.SaveChangesAsync();

        return department.Id;
    }

    private static WorkflowNodeDefinition ApprovalNode(string id, string name, ApprovalPolicy policy, IReadOnlyList<WorkflowAssignment> assignments, bool allowReject = true, bool allowReturn = true, bool allowDelegate = true, bool allowTransfer = true, bool allowAddApprover = true) =>
        new(id, WorkflowNodeType.ApprovalTask, name, Approval: new ApprovalConfig(policy, assignments, allowReject, allowReturn, allowDelegate, allowTransfer, allowAddApprover, new ReturnPolicy(allowReturn)));

    private static async Task<(ProcessDefinitionDto Definition, Guid TaskId)> PublishAndStartSingleApprovalAsync(ApprovalPolicy policy, IReadOnlyList<WorkflowAssignment> assignments, string? key = null)
    {
        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                ApprovalNode("approval", "Approval", policy, assignments),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "approval"),
                new WorkflowTransitionDefinition("t2", "approval", "end"),
            });

        key ??= $"approval-{Guid.NewGuid():N}";

        await using var setupDb = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(setupDb);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "Approval Test", null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var taskId = (await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id)).Id;

        return (definition, taskId);
    }

    // ---- Assignment resolution ----

    [Fact]
    public async Task Assignment_Role_ResolvesToAllRoleHolders()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var role = await CreateRoleWithMembersAsync(setupDb, userA, userB);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, role) });

        await using var db = PostgresFixture.CreateContext();
        var approval = await db.ApprovalInstances.Include(a => a.Assignments).SingleAsync(a => a.TaskInstanceId == taskId);
        Assert.Equal(2, approval.RequiredCount);
        Assert.Contains(approval.Assignments, a => a.UserId == userA);
        Assert.Contains(approval.Assignments, a => a.UserId == userB);
    }

    [Fact]
    public async Task Assignment_User_ResolvesToExactlyThatUser()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()) });

        await using var db = PostgresFixture.CreateContext();
        var approval = await db.ApprovalInstances.Include(a => a.Assignments).SingleAsync(a => a.TaskInstanceId == taskId);
        Assert.Single(approval.Assignments);
        Assert.Equal(userA, approval.Assignments.Single().UserId);
    }

    [Fact]
    public async Task Assignment_Department_ResolvesToAllDepartmentMembers()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var member1 = await CreateUserAsync(setupDb);
        var member2 = await CreateUserAsync(setupDb);
        var departmentId = await CreateDepartmentAsync(setupDb, managerUserId: null, member1, member2);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.Department, departmentId.ToString()) });

        await using var db = PostgresFixture.CreateContext();
        var approval = await db.ApprovalInstances.Include(a => a.Assignments).SingleAsync(a => a.TaskInstanceId == taskId);
        Assert.Equal(2, approval.RequiredCount);
    }

    [Fact]
    public async Task Assignment_DepartmentManager_ResolvesToTheManager()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var manager = await CreateUserAsync(setupDb);
        var departmentId = await CreateDepartmentAsync(setupDb, managerUserId: manager);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.DepartmentManager, departmentId.ToString()) });

        await using var db = PostgresFixture.CreateContext();
        var approval = await db.ApprovalInstances.Include(a => a.Assignments).SingleAsync(a => a.TaskInstanceId == taskId);
        Assert.Equal(manager, approval.Assignments.Single().UserId);
    }

    [Fact]
    public async Task Assignment_ProcessInitiator_ResolvesToWhoeverStartedTheInstance()
    {
        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                ApprovalNode("approval", "Approval", ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, "") }),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "approval"),
                new WorkflowTransitionDefinition("t2", "approval", "end"),
            });

        var key = $"initiator-{Guid.NewGuid():N}";
        await using var setupDb = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(setupDb);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "Initiator Test", null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        var initiator = Guid.NewGuid();
        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), initiator);

        await using var db = PostgresFixture.CreateContext();
        var taskForInitiator = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);
        var approval = await db.ApprovalInstances.Include(a => a.Assignments).SingleAsync(a => a.TaskInstanceId == taskForInitiator.Id);
        Assert.Equal(initiator, approval.Assignments.Single().UserId);
    }

    [Fact]
    public async Task PublishVersion_InvalidAssignmentType_RejectedAtPublishTime()
    {
        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                ApprovalNode("approval", "Approval", ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.Manager, "x") }),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "approval"),
                new WorkflowTransitionDefinition("t2", "approval", "end"),
            });

        var key = $"invalid-assign-{Guid.NewGuid():N}";
        await using var db = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(db);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "Invalid", null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() => NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid()));
        Assert.Contains(ex.Errors, e => e.Code == "UNSUPPORTED_ASSIGNMENT_TYPE");
    }

    // ---- Sequential ----

    [Fact]
    public async Task Sequential_LaterApproverCannotActBeforeEarlierOne()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(
            ApprovalPolicy.Sequential,
            new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()), new WorkflowAssignment(WorkflowAssignmentType.User, userB.ToString()) });

        await using var earlyDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() => NewEngine(earlyDb).ApproveTaskAsync(taskId, userB, Array.Empty<string>()));
        Assert.Equal("APPROVAL_NOT_ACTIVE", ex.Code);

        await using var aDb = PostgresFixture.CreateContext();
        var afterA = await NewEngine(aDb).ApproveTaskAsync(taskId, userA, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Pending, afterA.Status);

        await using var bDb = PostgresFixture.CreateContext();
        var afterB = await NewEngine(bDb).ApproveTaskAsync(taskId, userB, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, afterB.Status);
    }

    // ---- All ----

    [Fact]
    public async Task All_ProcessStaysPendingUntilEveryApproverHasApproved()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(
            ApprovalPolicy.All,
            new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()), new WorkflowAssignment(WorkflowAssignmentType.User, userB.ToString()) });

        await using var aDb = PostgresFixture.CreateContext();
        var afterA = await NewEngine(aDb).ApproveTaskAsync(taskId, userA, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Pending, afterA.Status);
        Assert.Equal(ApprovalInstanceStatus.Pending, afterA.Approval!.Status);

        await using var bDb = PostgresFixture.CreateContext();
        var afterB = await NewEngine(bDb).ApproveTaskAsync(taskId, userB, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, afterB.Status);
        Assert.Equal(ApprovalInstanceStatus.Approved, afterB.Approval!.Status);
    }

    // ---- AnyOne ----

    [Fact]
    public async Task AnyOne_FirstApprovalWins_OthersCancelled_WorkflowContinues()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);
        var userC = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(
            ApprovalPolicy.AnyOne,
            new[]
            {
                new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()),
                new WorkflowAssignment(WorkflowAssignmentType.User, userB.ToString()),
                new WorkflowAssignment(WorkflowAssignmentType.User, userC.ToString()),
            });

        await using var bDb = PostgresFixture.CreateContext();
        var result = await NewEngine(bDb).ApproveTaskAsync(taskId, userB, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, result.Status);

        var others = result.Approval!.Assignments.Where(a => a.UserId != userB).ToList();
        Assert.All(others, a => Assert.Equal(ApprovalAssignmentStatus.Cancelled, a.Status));

        await using var verifyDb = PostgresFixture.CreateContext();
        var taskForInstance = await verifyDb.TaskInstances.SingleAsync(t => t.Id == taskId);
        var instance = await verifyDb.ProcessInstances.SingleAsync(p => p.Id == taskForInstance.ProcessInstanceId);
        Assert.Equal(ProcessInstanceStatus.Completed, instance.Status);
    }

    [Fact]
    public async Task AnyOne_LateApprovalAfterResolution_IsConflict()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(
            ApprovalPolicy.AnyOne,
            new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()), new WorkflowAssignment(WorkflowAssignmentType.User, userB.ToString()) });

        await using var aDb = PostgresFixture.CreateContext();
        await NewEngine(aDb).ApproveTaskAsync(taskId, userA, Array.Empty<string>());

        // The whole TaskInstance already resolved to Completed once A's AnyOne approval won, so
        // this fails the task-level state check before it would even get to per-assignment state
        // (Skill.md §20's idempotency guarantee applies at the task level first).
        await using var bDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() => NewEngine(bDb).ApproveTaskAsync(taskId, userB, Array.Empty<string>()));
        Assert.Equal("TASK_ALREADY_COMPLETED", ex.Code);
    }

    // ---- Reject ----

    [Fact]
    public async Task Reject_UnderAllPolicy_TerminatesProcessImmediately()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(
            ApprovalPolicy.All,
            new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()), new WorkflowAssignment(WorkflowAssignmentType.User, userB.ToString()) });

        await using var db = PostgresFixture.CreateContext();
        var result = await NewEngine(db).RejectTaskAsync(taskId, userA, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Rejected, result.Status);

        await using var verifyDb = PostgresFixture.CreateContext();
        var task = await verifyDb.TaskInstances.SingleAsync(t => t.Id == taskId);
        var instance = await verifyDb.ProcessInstances.SingleAsync(p => p.Id == task.ProcessInstanceId);
        Assert.Equal(ProcessInstanceStatus.Rejected, instance.Status);
        Assert.NotNull(instance.CompletedAt);
    }

    [Fact]
    public async Task Reject_UnderAnyOnePolicy_OnlyTerminatesOnceEveryoneHasRejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(
            ApprovalPolicy.AnyOne,
            new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()), new WorkflowAssignment(WorkflowAssignmentType.User, userB.ToString()) });

        await using var aDb = PostgresFixture.CreateContext();
        await NewEngine(aDb).RejectTaskAsync(taskId, userA, Array.Empty<string>());

        await using var verifyDb1 = PostgresFixture.CreateContext();
        var taskForRunningCheck = await verifyDb1.TaskInstances.SingleAsync(t => t.Id == taskId);
        var stillRunning = await verifyDb1.ProcessInstances.SingleAsync(p => p.Id == taskForRunningCheck.ProcessInstanceId);
        Assert.Equal(ProcessInstanceStatus.Running, stillRunning.Status);

        await using var bDb = PostgresFixture.CreateContext();
        var result = await NewEngine(bDb).RejectTaskAsync(taskId, userB, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Rejected, result.Status);
    }

    // ---- Return ----

    [Fact]
    public async Task Return_SendsTaskToPreviousNode_ProcessStaysRunning()
    {
        var userA = Guid.Empty;
        var userB = Guid.Empty;
        await using (var setupDb = PostgresFixture.CreateContext())
        {
            userA = await CreateUserAsync(setupDb);
            userB = await CreateUserAsync(setupDb);
        }

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                ApprovalNode("a", "A", ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()) }),
                ApprovalNode("b", "B", ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userB.ToString()) }),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "a"),
                new WorkflowTransitionDefinition("t2", "a", "b"),
                new WorkflowTransitionDefinition("t3", "b", "end"),
            });

        var key = $"return-{Guid.NewGuid():N}";
        await using var setupDb2 = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(setupDb2);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "Return Test", null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), Guid.NewGuid());

        await using var db1 = PostgresFixture.CreateContext();
        var taskA = await db1.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);

        await using var approveDb = PostgresFixture.CreateContext();
        await NewEngine(approveDb).ApproveTaskAsync(taskA.Id, userA, Array.Empty<string>());

        await using var db2 = PostgresFixture.CreateContext();
        var taskB = await db2.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id && t.NodeId == "b");

        await using var returnDb = PostgresFixture.CreateContext();
        var returned = await NewEngine(returnDb).ReturnTaskAsync(taskB.Id, userB, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Returned, returned.Status);

        await using var verifyDb = PostgresFixture.CreateContext();
        var stillRunning = await verifyDb.ProcessInstances.SingleAsync(p => p.Id == instance.Id);
        Assert.Equal(ProcessInstanceStatus.Running, stillRunning.Status);

        var newTaskAtA = await verifyDb.TaskInstances
            .Where(t => t.ProcessInstanceId == instance.Id && t.NodeId == "a" && t.Status == TaskInstanceStatus.Pending)
            .SingleOrDefaultAsync();
        Assert.NotNull(newTaskAtA);
    }

    // ---- Delegate ----

    [Fact]
    public async Task Delegate_OriginalAndDelegateCanBothAct_UnrelatedUserCannot()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var owner = await CreateUserAsync(setupDb);
        var delegateUser = await CreateUserAsync(setupDb);
        var stranger = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, owner.ToString()) });

        await using var delegateDb = PostgresFixture.CreateContext();
        await NewEngine(delegateDb).DelegateTaskAsync(taskId, owner, delegateUser);

        await using var strangerDb = PostgresFixture.CreateContext();
        var forbidden = await Assert.ThrowsAsync<ForbiddenAppException>(() => NewEngine(strangerDb).ApproveTaskAsync(taskId, stranger, Array.Empty<string>()));
        Assert.Equal("APPROVAL_NOT_ASSIGNED", forbidden.Code);

        await using var actDb = PostgresFixture.CreateContext();
        var result = await NewEngine(actDb).ApproveTaskAsync(taskId, delegateUser, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Delegate_ByNonOwner_IsForbidden()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var owner = await CreateUserAsync(setupDb);
        var stranger = await CreateUserAsync(setupDb);
        var target = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, owner.ToString()) });

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() => NewEngine(db).DelegateTaskAsync(taskId, stranger, target));
        Assert.Equal("APPROVAL_NOT_ASSIGNED", ex.Code);
    }

    // ---- Transfer ----

    [Fact]
    public async Task Transfer_NewAssigneeCanAct_OldAssigneeCannot()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var original = await CreateUserAsync(setupDb);
        var newOwner = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, original.ToString()) });

        await using var transferDb = PostgresFixture.CreateContext();
        var afterTransfer = await NewEngine(transferDb).TransferTaskAsync(taskId, original, Array.Empty<string>(), newOwner, "handoff");
        Assert.Equal(newOwner, afterTransfer.Approval!.Assignments.Single().UserId);

        await using var oldDb = PostgresFixture.CreateContext();
        var forbidden = await Assert.ThrowsAsync<ForbiddenAppException>(() => NewEngine(oldDb).ApproveTaskAsync(taskId, original, Array.Empty<string>()));
        Assert.Equal("APPROVAL_NOT_ASSIGNED", forbidden.Code);

        await using var newDb = PostgresFixture.CreateContext();
        var result = await NewEngine(newDb).ApproveTaskAsync(taskId, newOwner, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, result.Status);
    }

    // ---- Add Approver ----

    [Fact]
    public async Task AddApprover_UnderAllPolicy_NewApproverBecomesRequired()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userD = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()) });

        await using var addDb = PostgresFixture.CreateContext();
        var afterAdd = await NewEngine(addDb).AddApproverAsync(taskId, Guid.NewGuid(), new[] { "Administrator" }, userD);
        Assert.Equal(2, afterAdd.Approval!.RequiredCount);

        await using var aDb = PostgresFixture.CreateContext();
        var afterA = await NewEngine(aDb).ApproveTaskAsync(taskId, userA, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Pending, afterA.Status);

        await using var dDb = PostgresFixture.CreateContext();
        var afterD = await NewEngine(dDb).ApproveTaskAsync(taskId, userD, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, afterD.Status);
    }

    [Fact]
    public async Task AddApprover_UnderAnyOnePolicy_DoesNotRaiseRequiredCount()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userD = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()) });

        await using var addDb = PostgresFixture.CreateContext();
        var afterAdd = await NewEngine(addDb).AddApproverAsync(taskId, Guid.NewGuid(), new[] { "Administrator" }, userD);
        Assert.Equal(1, afterAdd.Approval!.RequiredCount);
        Assert.Equal(2, afterAdd.Approval.Assignments.Count);
    }

    [Fact]
    public async Task AddApprover_ByNonAdmin_IsForbidden()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userD = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()) });

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() => NewEngine(db).AddApproverAsync(taskId, Guid.NewGuid(), Array.Empty<string>(), userD));
        Assert.Equal("ADD_APPROVER_REQUIRES_ADMIN", ex.Code);
    }

    // ---- Security ----

    [Fact]
    public async Task Approve_ByUnauthorizedUser_IsForbidden_NotSilentlyAllowed()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var owner = await CreateUserAsync(setupDb);
        var attacker = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, owner.ToString()) });

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() => NewEngine(db).ApproveTaskAsync(taskId, attacker, Array.Empty<string>()));
        Assert.Equal("APPROVAL_NOT_ASSIGNED", ex.Code);
    }

    [Fact]
    public async Task Approve_AfterProcessAlreadyCompleted_IsConflict()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var owner = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, owner.ToString()) });

        await using var db1 = PostgresFixture.CreateContext();
        await NewEngine(db1).ApproveTaskAsync(taskId, owner, Array.Empty<string>());

        await using var db2 = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() => NewEngine(db2).ApproveTaskAsync(taskId, owner, Array.Empty<string>()));
        Assert.Equal("TASK_ALREADY_COMPLETED", ex.Code);
    }

    [Fact]
    public async Task CompleteTask_OnApprovalTask_IsRejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var owner = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(ApprovalPolicy.All, new[] { new WorkflowAssignment(WorkflowAssignmentType.User, owner.ToString()) });

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() => NewEngine(db).CompleteTaskAsync(taskId, owner, Array.Empty<string>()));
        Assert.Equal("TASK_IS_APPROVAL_TASK", ex.Code);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task AnyOne_ConcurrentApprovals_ExactlyOneSucceeds()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(
            ApprovalPolicy.AnyOne,
            new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()), new WorkflowAssignment(WorkflowAssignmentType.User, userB.ToString()) });

        // Deterministic race, same technique as Phase 2's CompleteTask concurrency test: context2
        // tracks the ApprovalInstance row *before* context1 commits, so EF's identity map hands
        // the engine back that stale tracked instance rather than re-querying — exactly what two
        // truly concurrent requests would each have in memory.
        await using var context1 = PostgresFixture.CreateContext();
        await using var context2 = PostgresFixture.CreateContext();

        var taskForTracking = await context2.TaskInstances.Include(t => t.ProcessInstance).SingleAsync(t => t.Id == taskId);
        await context2.ApprovalInstances.Include(a => a.Assignments).SingleAsync(a => a.TaskInstanceId == taskId);

        await new Engine.WorkflowEngine(context1).ApproveTaskAsync(taskId, userA, Array.Empty<string>());

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() => new Engine.WorkflowEngine(context2).ApproveTaskAsync(taskId, userB, Array.Empty<string>()));
        Assert.Equal("TASK_CONFLICT", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        var approval = await verifyDb.ApprovalInstances.Include(a => a.Assignments).SingleAsync(a => a.TaskInstanceId == taskId);
        Assert.Equal(ApprovalInstanceStatus.Approved, approval.Status);
        Assert.Equal(1, approval.ApprovedCount);
        Assert.Equal(ApprovalAssignmentStatus.Approved, approval.Assignments.Single(a => a.UserId == userA).Status);
        // The loser's own mutation never committed (SaveChanges is all-or-nothing) — B's slot is
        // whatever the winning AnyOne resolution left it as (Cancelled), not stuck half-applied.
        Assert.Equal(ApprovalAssignmentStatus.Cancelled, approval.Assignments.Single(a => a.UserId == userB).Status);
    }

    [Fact]
    public async Task All_ConcurrentApprovals_BothEventuallyRecorded_SecondViaRetry()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var userA = await CreateUserAsync(setupDb);
        var userB = await CreateUserAsync(setupDb);

        var (_, taskId) = await PublishAndStartSingleApprovalAsync(
            ApprovalPolicy.All,
            new[] { new WorkflowAssignment(WorkflowAssignmentType.User, userA.ToString()), new WorkflowAssignment(WorkflowAssignmentType.User, userB.ToString()) });

        await using var context1 = PostgresFixture.CreateContext();
        await using var context2 = PostgresFixture.CreateContext();
        await context2.TaskInstances.Include(t => t.ProcessInstance).SingleAsync(t => t.Id == taskId);
        await context2.ApprovalInstances.Include(a => a.Assignments).SingleAsync(a => a.TaskInstanceId == taskId);

        await new Engine.WorkflowEngine(context1).ApproveTaskAsync(taskId, userA, Array.Empty<string>());

        // B's first attempt races against a stale tracked ApprovalInstance and loses...
        await Assert.ThrowsAsync<ConflictAppException>(() => new Engine.WorkflowEngine(context2).ApproveTaskAsync(taskId, userB, Array.Empty<string>()));

        // ...but the failed SaveChanges left nothing partially applied, so a plain retry against
        // fresh state succeeds and both approvals end up correctly recorded.
        await using var retryDb = PostgresFixture.CreateContext();
        var result = await NewEngine(retryDb).ApproveTaskAsync(taskId, userB, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, result.Status);
        Assert.Equal(2, result.Approval!.ApprovedCount);
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
