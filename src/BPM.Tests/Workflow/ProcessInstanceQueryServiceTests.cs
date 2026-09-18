using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Workflow;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Workflow;

// Phase 5.5.4 hardening: IProcessInstanceQueryService previously took no caller identity at all —
// any authenticated user could view any process instance by id, or list every instance in the
// system, via GET /api/process-instances[/{id}]. The exact same class of gap Phase 5.4.3 found
// and fixed for ITaskQueryService.GetByIdAsync (see that interface's own doc comment), just never
// applied to this sibling service. These tests cover the fix.
[Collection("Postgres")]
public class ProcessInstanceQueryServiceTests
{
    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);

    private static ProcessInstanceQueryService NewQueryService(BpmDbContext db) => new(db);

    private static WorkflowDefinition SequentialDefinition(string role) => new(
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

    private static async Task<(ProcessDefinitionDto Definition, string Role)> CreateAndPublishAsync()
    {
        var role = $"Role-{Guid.NewGuid():N}";
        var key = $"test-{Guid.NewGuid():N}";

        await using var setupDb = PostgresFixture.CreateContext();
        var definitionService = NewDefinitionService(setupDb);
        var definition = await definitionService.CreateAsync(new CreateProcessDefinitionRequest(key, "Test Process", null, null));
        await definitionService.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(SequentialDefinition(role)));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        return (definition, role);
    }

    [Fact]
    public async Task GetByIdAsync_Initiator_CanSeeOwnInstance()
    {
        var (definition, _) = await CreateAndPublishAsync();
        var initiatorId = Guid.NewGuid();

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(definition.Key, null), initiatorId);

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetByIdAsync(instance.Id, initiatorId, Array.Empty<string>());

        Assert.NotNull(result);
        Assert.Equal(instance.Id, result!.Id);
    }

    [Fact]
    public async Task GetByIdAsync_RoleAssignee_CanSeeInstance()
    {
        var (definition, role) = await CreateAndPublishAsync();

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetByIdAsync(instance.Id, Guid.NewGuid(), new[] { role });

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetByIdAsync_UnrelatedUser_Rejected()
    {
        var (definition, _) = await CreateAndPublishAsync();

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            NewQueryService(db).GetByIdAsync(instance.Id, Guid.NewGuid(), Array.Empty<string>()));
        Assert.Equal("PROCESS_INSTANCE_NOT_AUTHORIZED", ex.Code);
    }

    [Fact]
    public async Task GetByIdAsync_Administrator_CanSeeAnyInstance()
    {
        var (definition, _) = await CreateAndPublishAsync();

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewEngine(startDb).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetByIdAsync(instance.Id, Guid.NewGuid(), new[] { "Administrator" });

        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetAllAsync_NonAdministrator_OnlySeesOwnInstances()
    {
        var (definition, _) = await CreateAndPublishAsync();
        var myInitiatorId = Guid.NewGuid();

        await using var startDb1 = PostgresFixture.CreateContext();
        var myInstance = await NewEngine(startDb1).StartProcessAsync(new StartProcessRequest(definition.Key, null), myInitiatorId);
        await using var startDb2 = PostgresFixture.CreateContext();
        var otherInstance = await NewEngine(startDb2).StartProcessAsync(new StartProcessRequest(definition.Key, null), Guid.NewGuid());

        await using var db = PostgresFixture.CreateContext();
        var result = await NewQueryService(db).GetAllAsync(myInitiatorId, Array.Empty<string>());

        Assert.Contains(result, r => r.Id == myInstance.Id);
        Assert.DoesNotContain(result, r => r.Id == otherInstance.Id);
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
