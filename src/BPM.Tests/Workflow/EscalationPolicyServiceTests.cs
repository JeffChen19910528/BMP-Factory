using BPM.Application.Common;
using BPM.Application.Processes;
using BPM.Application.Sla;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Xunit;

namespace BPM.Tests.Workflow;

// Phase 6.4 — EscalationPolicyService CRUD, mirroring SlaPolicyServiceTests.cs's own structure
// exactly (same RowVersion/ExpectedVersion pattern, same test shape).
[Collection("Postgres")]
public class EscalationPolicyServiceTests
{
    private static EscalationPolicyService NewService(BpmDbContext db) =>
        new(db, new CreateEscalationPolicyRequestValidator(), new UpdateEscalationPolicyRequestValidator());

    private static ProcessDefinitionService NewDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static async Task<Guid> CreateProcessDefinitionAsync(BpmDbContext db)
    {
        var definition = await NewDefinitionService(db).CreateAsync(new CreateProcessDefinitionRequest($"esc-test-{Guid.NewGuid():N}", "Escalation Test Process", null, null));
        return definition.Id;
    }

    [Fact]
    public async Task CreateAsync_Valid_Succeeds()
    {
        await using var db = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(db);

        var policy = await NewService(db).CreateAsync(new CreateEscalationPolicyRequest(processId, "approval", true, 1440, WorkflowAssignmentType.Role, "Manager"));

        Assert.Equal("approval", policy.NodeId);
        Assert.True(policy.Enabled);
        Assert.Equal(WorkflowAssignmentType.Role, policy.TargetType);
        Assert.NotEmpty(policy.RowVersion);
    }

    [Fact]
    public async Task CreateAsync_ProcessInitiatorTarget_NoTargetValueRequired()
    {
        await using var db = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(db);

        var policy = await NewService(db).CreateAsync(new CreateEscalationPolicyRequest(processId, "approval", true, 60, WorkflowAssignmentType.ProcessInitiator, ""));

        Assert.Equal(WorkflowAssignmentType.ProcessInitiator, policy.TargetType);
    }

    [Fact]
    public async Task CreateAsync_DepartmentTargetType_Rejected()
    {
        // Part T: only User/Role/DepartmentManager/ProcessInitiator are valid escalation targets —
        // plain Department (escalate to "everyone in a department") is deliberately excluded.
        await using var db = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(db);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            NewService(db).CreateAsync(new CreateEscalationPolicyRequest(processId, "approval", true, 60, WorkflowAssignmentType.Department, Guid.NewGuid().ToString())));
    }

    [Fact]
    public async Task CreateAsync_NegativeDelay_Rejected()
    {
        await using var db = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(db);

        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() =>
            NewService(db).CreateAsync(new CreateEscalationPolicyRequest(processId, "approval", true, -1, WorkflowAssignmentType.Role, "Manager")));
    }

    [Fact]
    public async Task CreateAsync_InvalidProcessDefinition_Rejected()
    {
        await using var db = PostgresFixture.CreateContext();

        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).CreateAsync(new CreateEscalationPolicyRequest(Guid.NewGuid(), "approval", true, 60, WorkflowAssignmentType.Role, "Manager")));
        Assert.Equal("INVALID_PROCESS_DEFINITION", ex.Code);
    }

    [Fact]
    public async Task CreateAsync_DuplicateForSameProcessAndNode_Rejected()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(setupDb);
        await NewService(setupDb).CreateAsync(new CreateEscalationPolicyRequest(processId, "approval", true, 60, WorkflowAssignmentType.Role, "Manager"));

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).CreateAsync(new CreateEscalationPolicyRequest(processId, "approval", true, 120, WorkflowAssignmentType.Role, "Manager")));
        Assert.Equal("ESCALATION_POLICY_ALREADY_EXISTS", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_Valid_Succeeds_AndAdvancesRowVersion()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateEscalationPolicyRequest(processId, "approval", true, 60, WorkflowAssignmentType.Role, "Manager"));

        await using var updateDb = PostgresFixture.CreateContext();
        var updated = await NewService(updateDb).UpdateAsync(created.Id, new UpdateEscalationPolicyRequest(true, 120, WorkflowAssignmentType.Role, "Director", created.RowVersion));

        Assert.NotNull(updated);
        Assert.Equal(120, updated!.DelayMinutes);
        Assert.Equal("Director", updated.TargetValue);
        Assert.NotEqual(created.RowVersion, updated.RowVersion);
    }

    [Fact]
    public async Task UpdateAsync_StaleExpectedVersion_RejectedWithConflict()
    {
        await using var createDb = PostgresFixture.CreateContext();
        var processId = await CreateProcessDefinitionAsync(createDb);
        var created = await NewService(createDb).CreateAsync(new CreateEscalationPolicyRequest(processId, "approval", true, 60, WorkflowAssignmentType.Role, "Manager"));

        await using var writerADb = PostgresFixture.CreateContext();
        await NewService(writerADb).UpdateAsync(created.Id, new UpdateEscalationPolicyRequest(true, 90, WorkflowAssignmentType.Role, "Manager", created.RowVersion));

        await using var writerBDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(writerBDb).UpdateAsync(created.Id, new UpdateEscalationPolicyRequest(true, 120, WorkflowAssignmentType.Role, "Manager", created.RowVersion)));
        Assert.Equal("ESCALATION_POLICY_CONCURRENCY_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_NonexistentPolicy_ReturnsNull()
    {
        await using var db = PostgresFixture.CreateContext();
        var result = await NewService(db).UpdateAsync(Guid.NewGuid(), new UpdateEscalationPolicyRequest(true, 60, WorkflowAssignmentType.Role, "Manager", Convert.ToBase64String(new byte[8])));
        Assert.Null(result);
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
