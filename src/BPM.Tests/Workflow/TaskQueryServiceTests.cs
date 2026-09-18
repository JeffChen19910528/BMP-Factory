using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Workflow;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Workflow;

// Phase 5.4.3: TaskQueryService.GetByIdAsync gained a real authorization check — see its own doc
// comment on ITaskQueryService for the pre-existing IDOR gap this closes (previously any
// authenticated user could load any task's detail by id; nothing exercised this because Task
// Detail didn't exist as real UI before this phase).
[Collection("Postgres")]
public class TaskQueryServiceTests
{
    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);

    private static TaskQueryService NewQueryService(BpmDbContext db) => new(db);

    private static async Task<Guid> StartProcessWithUserAssignedTaskAsync(Guid assigneeId)
    {
        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Task", new WorkflowAssignment(WorkflowAssignmentType.User, assigneeId.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "task"),
                new WorkflowTransitionDefinition("t2", "task", "end"),
            });

        var key = $"task-auth-{Guid.NewGuid():N}";
        await using var setupDb = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(setupDb);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "Task Auth Test", null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        await using var startDb = PostgresFixture.CreateContext();
        var started = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), Guid.NewGuid());

        await using var taskDb = PostgresFixture.CreateContext();
        var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == started.Id);
        return task.Id;
    }

    [Fact]
    public async Task GetById_Assignee_CanViewTheirOwnTask()
    {
        var assignee = Guid.NewGuid();
        var taskId = await StartProcessWithUserAssignedTaskAsync(assignee);

        await using var db = PostgresFixture.CreateContext();
        var task = await NewQueryService(db).GetByIdAsync(taskId, assignee, Array.Empty<string>());

        Assert.NotNull(task);
        Assert.Equal(taskId, task!.Id);
    }

    [Fact]
    public async Task GetById_UnrelatedUser_IsForbidden()
    {
        var assignee = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var taskId = await StartProcessWithUserAssignedTaskAsync(assignee);

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() => NewQueryService(db).GetByIdAsync(taskId, stranger, Array.Empty<string>()));
        Assert.Equal("TASK_NOT_AUTHORIZED", ex.Code);
    }

    [Fact]
    public async Task GetById_Administrator_CanViewAnyTask()
    {
        var assignee = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var taskId = await StartProcessWithUserAssignedTaskAsync(assignee);

        await using var db = PostgresFixture.CreateContext();
        var task = await NewQueryService(db).GetByIdAsync(taskId, admin, new[] { "Administrator" });

        Assert.NotNull(task);
    }

    [Fact]
    public async Task GetById_NonexistentTask_ReturnsNull()
    {
        await using var db = PostgresFixture.CreateContext();
        var task = await NewQueryService(db).GetByIdAsync(Guid.NewGuid(), Guid.NewGuid(), Array.Empty<string>());

        Assert.Null(task);
    }

    // Phase 12 — GetMyTasksAsync previously had no pagination at all (an unbounded fetch of every
    // task the caller has ever been assignee/role-eligible/approval-participant on). These tests
    // cover the new PagedResult<TaskDto> shape: correct Page/PageSize/TotalCount, bounded item
    // count, deterministic CreatedAt-descending ordering preserved, extreme-Page safety, and — the
    // one thing that must never regress — the existing per-caller authorization scope (a user only
    // ever sees their own tasks, regardless of how pagination slices the result).

    private static async Task<Guid> CreateUserAssignedTaskAsync(string key)
    {
        await using var startDb = PostgresFixture.CreateContext();
        var started = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(key, null), Guid.NewGuid());

        await using var taskDb = PostgresFixture.CreateContext();
        var task = await taskDb.TaskInstances.SingleAsync(t => t.ProcessInstanceId == started.Id);
        return task.Id;
    }

    private static async Task<string> PublishSingleUserTaskDefinitionAsync(Guid assignee)
    {
        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Task", new WorkflowAssignment(WorkflowAssignmentType.User, assignee.ToString())),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "task"),
                new WorkflowTransitionDefinition("t2", "task", "end"),
            });

        var key = $"my-tasks-page-{Guid.NewGuid():N}";
        await using var setupDb = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(setupDb);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "My Tasks Pagination Test", null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        return key;
    }

    [Fact]
    public async Task GetMyTasksAsync_Pagination_ReturnsCorrectPageAndTotalCount()
    {
        var assignee = Guid.NewGuid();
        var key = await PublishSingleUserTaskDefinitionAsync(assignee);

        for (var i = 0; i < 5; i++)
        {
            await CreateUserAssignedTaskAsync(key);
        }

        await using var db = PostgresFixture.CreateContext();
        var page1 = await NewQueryService(db).GetMyTasksAsync(assignee, Array.Empty<string>(), new MyTasksQuery(Page: 1, PageSize: 2));

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(1, page1.Page);
        Assert.Equal(2, page1.PageSize);

        await using var db2 = PostgresFixture.CreateContext();
        var page3 = await NewQueryService(db2).GetMyTasksAsync(assignee, Array.Empty<string>(), new MyTasksQuery(Page: 3, PageSize: 2));
        Assert.Single(page3.Items); // 5 items, page size 2 -> page 3 has the last remaining item
    }

    [Fact]
    public async Task GetMyTasksAsync_ExtremelyLargePage_DoesNotThrow_ReturnsEmptyResult()
    {
        var assignee = Guid.NewGuid();
        var key = await PublishSingleUserTaskDefinitionAsync(assignee);
        await CreateUserAssignedTaskAsync(key);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetMyTasksAsync(assignee, Array.Empty<string>(), new MyTasksQuery(Page: int.MaxValue, PageSize: 200));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetMyTasksAsync_AuthorizationRegression_OnlyReturnsCallersOwnTasks()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var keyA = await PublishSingleUserTaskDefinitionAsync(userA);
        var keyB = await PublishSingleUserTaskDefinitionAsync(userB);
        await CreateUserAssignedTaskAsync(keyA);
        await CreateUserAssignedTaskAsync(keyB);

        await using var db = PostgresFixture.CreateContext();
        var resultA = await NewQueryService(db).GetMyTasksAsync(userA, Array.Empty<string>(), new MyTasksQuery());
        Assert.Single(resultA.Items);
        Assert.Equal(userA, resultA.Items[0].AssigneeId);

        await using var db2 = PostgresFixture.CreateContext();
        var resultB = await NewQueryService(db2).GetMyTasksAsync(userB, Array.Empty<string>(), new MyTasksQuery());
        Assert.Single(resultB.Items);
        Assert.Equal(userB, resultB.Items[0].AssigneeId);
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
