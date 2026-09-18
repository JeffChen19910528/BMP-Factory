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

// End-to-end coverage of the Phase 2 workflow core against a real PostgreSQL database (see
// PostgresFixture). Exercises exactly the round trip Skill.md §35 describes: definition ->
// version -> validation -> runtime -> task -> transition -> completion -> audit.
[Collection("Postgres")]
public class WorkflowEngineIntegrationTests
{
    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);

    private static WorkflowDefinition SequentialDefinition(string role = "Manager") => new(
        Nodes: new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
            new WorkflowNodeDefinition("approval", WorkflowNodeType.UserTask, "Approval", new WorkflowAssignment(WorkflowAssignmentType.Role, role)),
            new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
        },
        Transitions: new[]
        {
            new WorkflowTransitionDefinition("t1", "start", "approval"),
            new WorkflowTransitionDefinition("t2", "approval", "end"),
        });

    private static WorkflowDefinition TwoTaskDefinition(string roleA, string roleB) => new(
        Nodes: new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
            new WorkflowNodeDefinition("a", WorkflowNodeType.UserTask, "Task A", new WorkflowAssignment(WorkflowAssignmentType.Role, roleA)),
            new WorkflowNodeDefinition("b", WorkflowNodeType.UserTask, "Task B", new WorkflowAssignment(WorkflowAssignmentType.Role, roleB)),
            new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
        },
        Transitions: new[]
        {
            new WorkflowTransitionDefinition("t1", "start", "a"),
            new WorkflowTransitionDefinition("t2", "a", "b"),
            new WorkflowTransitionDefinition("t3", "b", "end"),
        });

    private static async Task<ProcessDefinitionDto> CreateAndPublishAsync(WorkflowDefinition graph, string? key = null)
    {
        key ??= $"test-{Guid.NewGuid():N}";

        await using var setupDb = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(setupDb);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "Test Process", null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        return definition;
    }

    [Fact]
    public async Task StartProcess_CreatesProcessInstance_AndFirstTask()
    {
        var definition = await CreateAndPublishAsync(SequentialDefinition());

        await using var db = PostgresFixture.CreateContext();
        var instance = await NewEngine(db).StartProcessAsync(new StartProcessRequest(definition.Key, "BK-1"), Guid.NewGuid());

        Assert.Equal(ProcessInstanceStatus.Running, instance.Status);

        await using var verifyDb = PostgresFixture.CreateContext();
        var task = await verifyDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);
        Assert.Equal("approval", task.NodeId);
        Assert.Equal(TaskInstanceStatus.Pending, task.Status);
        Assert.Equal("Manager", task.AssigneeRole);
    }

    [Fact]
    public async Task CompleteTask_OnTwoTaskWorkflow_CreatesNextTask_ThenCompletesProcessOnFinalTask()
    {
        var role1 = $"RoleA-{Guid.NewGuid():N}";
        var role2 = $"RoleB-{Guid.NewGuid():N}";
        var definition = await CreateAndPublishAsync(TwoTaskDefinition(role1, role2));

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        await using var db1 = PostgresFixture.CreateContext();
        var firstTask = await db1.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);
        Assert.Equal("a", firstTask.NodeId);

        var completerA = Guid.NewGuid();
        await using var completeDb1 = PostgresFixture.CreateContext();
        await NewEngine(completeDb1).CompleteTaskAsync(firstTask.Id, completerA, new[] { role1 });

        await using var db2 = PostgresFixture.CreateContext();
        var secondTask = await db2.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id && t.NodeId == "b");
        Assert.Equal(TaskInstanceStatus.Pending, secondTask.Status);

        var stillRunning = await db2.ProcessInstances.SingleAsync(p => p.Id == instance.Id);
        Assert.Equal(ProcessInstanceStatus.Running, stillRunning.Status);

        var completerB = Guid.NewGuid();
        await using var completeDb2 = PostgresFixture.CreateContext();
        await NewEngine(completeDb2).CompleteTaskAsync(secondTask.Id, completerB, new[] { role2 });

        await using var db3 = PostgresFixture.CreateContext();
        var finished = await db3.ProcessInstances.SingleAsync(p => p.Id == instance.Id);
        Assert.Equal(ProcessInstanceStatus.Completed, finished.Status);
        Assert.NotNull(finished.CompletedAt);
    }

    [Fact]
    public async Task CompleteTask_UserWithoutTheRequiredRole_IsForbidden()
    {
        var role = $"Role-{Guid.NewGuid():N}";
        var definition = await CreateAndPublishAsync(SequentialDefinition(role));

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var task = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);

        await using var completeDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            NewEngine(completeDb).CompleteTaskAsync(task.Id, Guid.NewGuid(), Array.Empty<string>()));

        Assert.Equal("TASK_ROLE_NOT_HELD", ex.Code);
    }

    [Fact]
    public async Task CompleteTask_AlreadyCompleted_ReturnsConflictInsteadOfDuplicating()
    {
        var role = $"Role-{Guid.NewGuid():N}";
        var definition = await CreateAndPublishAsync(SequentialDefinition(role));

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var task = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);

        var completer = Guid.NewGuid();
        await using (var firstAttempt = PostgresFixture.CreateContext())
        {
            await NewEngine(firstAttempt).CompleteTaskAsync(task.Id, completer, new[] { role });
        }

        // A retried request (e.g. a network-level double-submit) must not create a second task
        // or re-complete the process — Skill.md §20.
        await using var secondAttempt = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewEngine(secondAttempt).CompleteTaskAsync(task.Id, completer, new[] { role }));
        Assert.Equal("TASK_ALREADY_COMPLETED", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        var taskCount = await verifyDb.TaskInstances.CountAsync(t => t.ProcessInstanceId == instance.Id);
        Assert.Equal(1, taskCount);
    }

    [Fact]
    public async Task CompleteTask_ConcurrentCompletions_ExactlyOneSucceeds()
    {
        var role = $"Role-{Guid.NewGuid():N}";
        var definition = await CreateAndPublishAsync(SequentialDefinition(role));

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var taskId = (await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id)).Id;

        // Two independent DbContexts racing to complete the same row (Skill.md §19).
        // Deterministic rather than relying on real thread timing: context2 loads (and tracks)
        // the task *before* context1 commits, so EF's identity map hands the engine back that
        // same stale tracked instance on its own query rather than re-fetching — exactly what a
        // truly concurrent request would have in memory. context1 then commits first; context2's
        // save uses the now-stale original RowVersion in its WHERE clause and must lose.
        await using var context1 = PostgresFixture.CreateContext();
        await using var context2 = PostgresFixture.CreateContext();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        await context2.TaskInstances.Include(t => t.ProcessInstance).SingleAsync(t => t.Id == taskId);

        await NewEngine(context1).CompleteTaskAsync(taskId, userA, new[] { role });

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewEngine(context2).CompleteTaskAsync(taskId, userB, new[] { role }));
        Assert.Equal("TASK_CONFLICT", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        var taskCount = await verifyDb.TaskInstances.CountAsync(t => t.ProcessInstanceId == instance.Id);
        Assert.Equal(1, taskCount);
    }

    [Fact]
    public async Task CompleteTask_FailureDuringTransition_LeavesTaskUnchanged()
    {
        var role = $"Role-{Guid.NewGuid():N}";
        var definition = await CreateAndPublishAsync(SequentialDefinition(role));

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var task = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);

        // Corrupt the published version's definition directly (bypassing the immutability rule
        // on purpose) to force AdvanceFrom to throw after the task's Status has already been
        // mutated in memory but before SaveChangesAsync is reached — proving that failure leaves
        // no partial write behind (Skill.md §18, §31).
        var version = await db.ProcessVersions.SingleAsync(v => v.Id == instance.ProcessVersionId);
        version.DefinitionJson = "{ not valid json";
        await db.SaveChangesAsync();

        await using var completeDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewEngine(completeDb).CompleteTaskAsync(task.Id, Guid.NewGuid(), new[] { role }));

        await using var verifyDb = PostgresFixture.CreateContext();
        var unchanged = await verifyDb.TaskInstances.SingleAsync(t => t.Id == task.Id);
        Assert.Equal(TaskInstanceStatus.Pending, unchanged.Status);
        Assert.Null(unchanged.CompletedAt);
    }

    [Fact]
    public async Task CreateProcessDefinition_DuplicateKey_Rejected()
    {
        var key = $"dup-{Guid.NewGuid():N}";
        await using var db = PostgresFixture.CreateContext();
        var service = NewDefinitionService(db);
        await service.CreateAsync(new CreateProcessDefinitionRequest(key, "First", null, null));

        await using var db2 = PostgresFixture.CreateContext();
        var service2 = NewDefinitionService(db2);
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            service2.CreateAsync(new CreateProcessDefinitionRequest(key, "Second", null, null)));

        Assert.Equal("PROCESS_DEFINITION_KEY_TAKEN", ex.Code);
    }

    [Fact]
    public async Task PublishVersion_InvalidGraph_RejectedAndNotPublished()
    {
        var invalid = new WorkflowDefinition(
            Nodes: new[] { new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start") },
            Transitions: Array.Empty<WorkflowTransitionDefinition>());

        var key = $"invalid-{Guid.NewGuid():N}";
        await using var db = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(db);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "Invalid", null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(invalid));

        await using var publishDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() =>
            NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid()));
        Assert.Contains(ex.Errors, e => e.Code == "MISSING_END_NODE");

        await using var verifyDb = PostgresFixture.CreateContext();
        var stillDraft = await verifyDb.ProcessDefinitions.SingleAsync(p => p.Id == definition.Id);
        Assert.Equal(ProcessDefinitionStatus.Draft, stillDraft.Status);
        Assert.Null(stillDraft.CurrentVersionId);
    }

    [Fact]
    public async Task StartProcess_FromDraftDefinition_IsRejected()
    {
        var key = $"draft-{Guid.NewGuid():N}";
        await using var db = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(db);
        await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "Never Published", null, null));

        await using var engineDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewEngine(engineDb).StartProcessAsync(new StartProcessRequest(key, null), Guid.NewGuid()));
        Assert.Equal("PROCESS_DEFINITION_NOT_PUBLISHED", ex.Code);
    }

    [Fact]
    public async Task CompleteTask_DirectUserAssignment_OnlyThatUserCanComplete()
    {
        var assignee = Guid.NewGuid();
        var definition = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("approval", WorkflowNodeType.UserTask, "Approval", new WorkflowAssignment(WorkflowAssignmentType.User, assignee.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "approval"),
                new WorkflowTransitionDefinition("t2", "approval", "end"),
            });
        var created = await CreateAndPublishAsync(definition);

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(created.Key, null), Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var task = await db.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id);
        Assert.Equal(assignee, task.AssigneeId);

        await using var wrongUserDb = PostgresFixture.CreateContext();
        var forbidden = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            NewEngine(wrongUserDb).CompleteTaskAsync(task.Id, Guid.NewGuid(), Array.Empty<string>()));
        Assert.Equal("TASK_NOT_ASSIGNED_TO_USER", forbidden.Code);

        await using var correctUserDb = PostgresFixture.CreateContext();
        var result = await NewEngine(correctUserDb).CompleteTaskAsync(task.Id, assignee, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, result.Status);
    }

    // Phase 10 — Process Start idempotency (MUST HAVE #1). BusinessKey uniqueness is scoped to
    // (TenantId, ProcessDefinitionId) — see ProcessInstanceConfiguration.cs's own comment — so
    // each test below uses its own freshly-created process definition.

    [Fact]
    public async Task StartProcess_WithoutBusinessKey_AllowsMultipleInstances()
    {
        var definition = await CreateAndPublishAsync(SequentialDefinition());

        await using var db1 = PostgresFixture.CreateContext();
        var instance1 = await NewEngine(db1).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        await using var db2 = PostgresFixture.CreateContext();
        var instance2 = await NewEngine(db2).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        Assert.NotEqual(instance1.Id, instance2.Id);
    }

    [Fact]
    public async Task StartProcess_WithUniqueBusinessKey_Succeeds()
    {
        var definition = await CreateAndPublishAsync(SequentialDefinition());

        await using var db = PostgresFixture.CreateContext();
        var instance = await NewEngine(db).StartProcessAsync(new StartProcessRequest(definition.Key, $"BK-{Guid.NewGuid():N}"), Guid.NewGuid());

        Assert.Equal(ProcessInstanceStatus.Running, instance.Status);
    }

    [Fact]
    public async Task StartProcess_SameBusinessKeyTwice_SecondAttemptRejectedCleanly_NoSecondInstance()
    {
        var definition = await CreateAndPublishAsync(SequentialDefinition());
        var businessKey = $"BK-{Guid.NewGuid():N}";

        await using var db1 = PostgresFixture.CreateContext();
        var first = await NewEngine(db1).StartProcessAsync(new StartProcessRequest(definition.Key, businessKey), Guid.NewGuid());

        await using var db2 = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewEngine(db2).StartProcessAsync(new StartProcessRequest(definition.Key, businessKey), Guid.NewGuid()));
        Assert.Equal("PROCESS_INSTANCE_DUPLICATE_BUSINESS_KEY", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        var count = await verifyDb.ProcessInstances.CountAsync(p => p.ProcessDefinitionId == first.ProcessDefinitionId && p.BusinessKey == businessKey);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task StartProcess_TwoConcurrentRequestsWithSameBusinessKey_ExactlyOneSucceeds_NoRawException()
    {
        var definition = await CreateAndPublishAsync(SequentialDefinition());
        var businessKey = $"BK-{Guid.NewGuid():N}";

        await using var db1 = PostgresFixture.CreateContext();
        await using var db2 = PostgresFixture.CreateContext();

        var attempt1 = NewEngine(db1).StartProcessAsync(new StartProcessRequest(definition.Key, businessKey), Guid.NewGuid());
        var attempt2 = NewEngine(db2).StartProcessAsync(new StartProcessRequest(definition.Key, businessKey), Guid.NewGuid());

        var results = await Task.WhenAll(attempt1.ContinueWith(t => t), attempt2.ContinueWith(t => t));

        var succeeded = results.Count(t => t.IsCompletedSuccessfully);
        var conflicted = results.Count(t => t.IsFaulted && t.Exception!.InnerExceptions.Single() is ConflictAppException { Code: "PROCESS_INSTANCE_DUPLICATE_BUSINESS_KEY" });

        Assert.Equal(1, succeeded);
        Assert.Equal(1, conflicted);

        await using var verifyDb = PostgresFixture.CreateContext();
        var definitionId = await verifyDb.ProcessDefinitions.Where(d => d.Key == definition.Key).Select(d => d.Id).SingleAsync();
        var count = await verifyDb.ProcessInstances.CountAsync(p => p.ProcessDefinitionId == definitionId && p.BusinessKey == businessKey);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task StartProcess_DifferentBusinessKeys_BothSucceed()
    {
        var definition = await CreateAndPublishAsync(SequentialDefinition());

        await using var db1 = PostgresFixture.CreateContext();
        var instance1 = await NewEngine(db1).StartProcessAsync(new StartProcessRequest(definition.Key, $"BK-{Guid.NewGuid():N}"), Guid.NewGuid());

        await using var db2 = PostgresFixture.CreateContext();
        var instance2 = await NewEngine(db2).StartProcessAsync(new StartProcessRequest(definition.Key, $"BK-{Guid.NewGuid():N}"), Guid.NewGuid());

        Assert.NotEqual(instance1.Id, instance2.Id);
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
