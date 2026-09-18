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

// Phase 8 — Process Governance & Lifecycle. Setup helpers mirror ProcessDefinitionServiceTests.cs's
// own self-contained-per-file style.
[Collection("Postgres")]
public class ProcessGovernanceTests
{
    private const string AdministratorRole = "Administrator";

    private static ProcessDefinitionService NewService(BpmDbContext db, Guid? currentUserId = null) =>
        new(db, new AuditService(db, new FixedCurrentUser(currentUserId)), new FixedCurrentUser(currentUserId),
            new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static Engine.WorkflowEngine NewEngine(BpmDbContext db) => new(db);

    private static WorkflowDefinition MinimalDefinition(string taskName = "Approval") => new(
        Nodes: new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
            new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, taskName, new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, "")),
            new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
        },
        Transitions: new[]
        {
            new WorkflowTransitionDefinition("t1", "start", "task"),
            new WorkflowTransitionDefinition("t2", "task", "end"),
        });

    private static async Task<Guid> CreateUserAsync(BpmDbContext db, bool isActive = true)
    {
        var user = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = "Test User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = isActive };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<ProcessDefinitionDto> CreateAndPublishAsync(BpmDbContext db, Guid creatorId, string? changeReason = null, string taskName = "Approval")
    {
        var definition = await NewService(db, creatorId).CreateAsync(new CreateProcessDefinitionRequest($"gov-{Guid.NewGuid():N}", "Governance Test", null, null));
        await NewService(db, creatorId).CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(MinimalDefinition(taskName)));
        await NewEngine(db).PublishVersionAsync(definition.Id, creatorId, changeReason);
        return (await NewService(db).GetByIdAsync(definition.Id))!;
    }

    // ---- Suspend ----

    [Fact]
    public async Task Suspend_FromPublished_Succeeds_AndBlocksNewStarts()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);

        var suspended = await NewService(db, admin).SuspendAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));

        Assert.Equal(ProcessDefinitionStatus.Suspended, suspended.Status);
        Assert.Equal(definition.CurrentVersionId, suspended.CurrentVersionId); // untouched

        var startEx = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewEngine(db).StartProcessAsync(new StartProcessRequest(GetKey(db, definition.Id), null), admin));
        Assert.Equal("PROCESS_DEFINITION_NOT_PUBLISHED", startEx.Code);
    }

    [Fact]
    public async Task Suspend_FromDraft_ThrowsInvalidTransition()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await NewService(db, admin).CreateAsync(new CreateProcessDefinitionRequest($"gov-{Guid.NewGuid():N}", "Draft Only", null, null));

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).SuspendAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion)));
        Assert.Equal("INVALID_LIFECYCLE_TRANSITION", ex.Code);
    }

    [Fact]
    public async Task Suspend_AlreadySuspended_ThrowsInvalidTransition()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var suspended = await NewService(db).SuspendAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).SuspendAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(suspended.RowVersion)));
        Assert.Equal("INVALID_LIFECYCLE_TRANSITION", ex.Code);
    }

    // ---- Archive ----

    [Fact]
    public async Task Archive_FromPublished_Succeeds()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);

        var archived = await NewService(db).ArchiveAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));

        Assert.Equal(ProcessDefinitionStatus.Archived, archived.Status);
    }

    [Fact]
    public async Task Archive_FromSuspended_Succeeds()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var suspended = await NewService(db).SuspendAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));

        var archived = await NewService(db).ArchiveAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(suspended.RowVersion));

        Assert.Equal(ProcessDefinitionStatus.Archived, archived.Status);
    }

    [Fact]
    public async Task Archive_FromDraft_ThrowsInvalidTransition()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await NewService(db, admin).CreateAsync(new CreateProcessDefinitionRequest($"gov-{Guid.NewGuid():N}", "Draft Only", null, null));

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).ArchiveAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion)));
        Assert.Equal("INVALID_LIFECYCLE_TRANSITION", ex.Code);
    }

    // ---- Restore ----

    [Fact]
    public async Task Restore_FromSuspended_Succeeds_AndNewStartWorksAgain()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var suspended = await NewService(db).SuspendAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));

        var restored = await NewService(db).RestoreAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(suspended.RowVersion));
        Assert.Equal(ProcessDefinitionStatus.Published, restored.Status);
        Assert.Equal(definition.CurrentVersionId, restored.CurrentVersionId);

        var instance = await NewEngine(db).StartProcessAsync(new StartProcessRequest(GetKey(db, definition.Id), null), admin);
        Assert.NotEqual(Guid.Empty, instance.Id);
    }

    [Fact]
    public async Task Restore_FromArchived_Succeeds()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var archived = await NewService(db).ArchiveAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));

        var restored = await NewService(db).RestoreAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(archived.RowVersion));

        Assert.Equal(ProcessDefinitionStatus.Published, restored.Status);
    }

    [Fact]
    public async Task Restore_FromPublished_ThrowsInvalidTransition()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).RestoreAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion)));
        Assert.Equal("INVALID_LIFECYCLE_TRANSITION", ex.Code);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task Suspend_StaleExpectedVersion_ThrowsConcurrencyConflict()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);

        // Someone else's action changes RowVersion first (a metadata update is enough).
        await NewService(db, admin).UpdateAsync(definition.Id, new UpdateProcessDefinitionRequest("Renamed", null, null));

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).SuspendAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion)));
        Assert.Equal("PROCESS_DEFINITION_CONCURRENCY_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task AssignOwner_StaleExpectedVersion_ThrowsConcurrencyConflict()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var owner = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);

        await NewService(db, admin).UpdateAsync(definition.Id, new UpdateProcessDefinitionRequest("Renamed", null, null));

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db).AssignOwnerAsync(definition.Id, new AssignProcessOwnerRequest(owner, definition.RowVersion)));
        Assert.Equal("PROCESS_DEFINITION_CONCURRENCY_CONFLICT", ex.Code);
    }

    // ---- Owner ----

    [Fact]
    public async Task AssignOwner_ValidActiveUser_Succeeds()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var owner = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);

        var updated = await NewService(db).AssignOwnerAsync(definition.Id, new AssignProcessOwnerRequest(owner, definition.RowVersion));

        Assert.Equal(owner, updated.OwnerUserId);
    }

    [Fact]
    public async Task AssignOwner_Change_Succeeds()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var owner1 = await CreateUserAsync(db);
        var owner2 = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var withOwner1 = await NewService(db).AssignOwnerAsync(definition.Id, new AssignProcessOwnerRequest(owner1, definition.RowVersion));

        var withOwner2 = await NewService(db).AssignOwnerAsync(definition.Id, new AssignProcessOwnerRequest(owner2, withOwner1.RowVersion));

        Assert.Equal(owner2, withOwner2.OwnerUserId);
    }

    [Fact]
    public async Task AssignOwner_Clear_SetsNull()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var owner = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var withOwner = await NewService(db).AssignOwnerAsync(definition.Id, new AssignProcessOwnerRequest(owner, definition.RowVersion));

        var cleared = await NewService(db).AssignOwnerAsync(definition.Id, new AssignProcessOwnerRequest(null, withOwner.RowVersion));

        Assert.Null(cleared.OwnerUserId);
    }

    [Fact]
    public async Task AssignOwner_NonexistentUser_ThrowsBadRequest()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);

        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).AssignOwnerAsync(definition.Id, new AssignProcessOwnerRequest(Guid.NewGuid(), definition.RowVersion)));
        Assert.Equal("INVALID_OWNER", ex.Code);
    }

    [Fact]
    public async Task AssignOwner_InactiveUser_ThrowsBadRequest()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var inactiveUser = await CreateUserAsync(db, isActive: false);
        var definition = await CreateAndPublishAsync(db, admin);

        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewService(db).AssignOwnerAsync(definition.Id, new AssignProcessOwnerRequest(inactiveUser, definition.RowVersion)));
        Assert.Equal("INVALID_OWNER", ex.Code);
    }

    // ---- Owner / Administrator / unrelated-user authorization ----

    [Fact]
    public async Task Suspend_ByOwner_Succeeds_WithoutAdministratorRole()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var owner = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var withOwner = await NewService(db).AssignOwnerAsync(definition.Id, new AssignProcessOwnerRequest(owner, definition.RowVersion));

        var suspended = await NewService(db).SuspendAsync(definition.Id, owner, Array.Empty<string>(), new ProcessLifecycleActionRequest(withOwner.RowVersion));

        Assert.Equal(ProcessDefinitionStatus.Suspended, suspended.Status);
    }

    [Fact]
    public async Task Suspend_ByUnrelatedUser_ThrowsForbidden()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var owner = await CreateUserAsync(db);
        var unrelated = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var withOwner = await NewService(db).AssignOwnerAsync(definition.Id, new AssignProcessOwnerRequest(owner, definition.RowVersion));

        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            NewService(db).SuspendAsync(definition.Id, unrelated, Array.Empty<string>(), new ProcessLifecycleActionRequest(withOwner.RowVersion)));
        Assert.Equal("PROCESS_DEFINITION_NOT_AUTHORIZED", ex.Code);
    }

    [Fact]
    public async Task Suspend_NoOwnerAssigned_NonAdministrator_ThrowsForbidden()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var unrelated = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);

        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            NewService(db).SuspendAsync(definition.Id, unrelated, Array.Empty<string>(), new ProcessLifecycleActionRequest(definition.RowVersion)));
        Assert.Equal("PROCESS_DEFINITION_NOT_AUTHORIZED", ex.Code);
    }

    [Fact]
    public async Task Suspend_ByOwnerOnUnrelatedDefinition_ThrowsForbidden()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var ownerOfA = await CreateUserAsync(db);
        var definitionA = await CreateAndPublishAsync(db, admin);
        var definitionB = await CreateAndPublishAsync(db, admin);
        await NewService(db).AssignOwnerAsync(definitionA.Id, new AssignProcessOwnerRequest(ownerOfA, definitionA.RowVersion));

        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            NewService(db).SuspendAsync(definitionB.Id, ownerOfA, Array.Empty<string>(), new ProcessLifecycleActionRequest(definitionB.RowVersion)));
        Assert.Equal("PROCESS_DEFINITION_NOT_AUTHORIZED", ex.Code);
    }

    // ---- Metadata edit status gate ----

    [Fact]
    public async Task UpdateMetadata_WhileSuspended_ThrowsConflict()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        await NewService(db).SuspendAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db, admin).UpdateAsync(definition.Id, new UpdateProcessDefinitionRequest("New Name", null, null)));
        Assert.Equal("PROCESS_DEFINITION_NOT_EDITABLE", ex.Code);
    }

    [Fact]
    public async Task UpdateMetadata_WhileArchived_ThrowsConflict()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        await NewService(db).ArchiveAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db, admin).UpdateAsync(definition.Id, new UpdateProcessDefinitionRequest("New Name", null, null)));
        Assert.Equal("PROCESS_DEFINITION_NOT_EDITABLE", ex.Code);
    }

    [Fact]
    public async Task UpdateMetadata_WhileDraftOrPublished_Succeeds()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var draft = await NewService(db, admin).CreateAsync(new CreateProcessDefinitionRequest($"gov-{Guid.NewGuid():N}", "Draft", null, null));
        var updatedDraft = await NewService(db, admin).UpdateAsync(draft.Id, new UpdateProcessDefinitionRequest("Draft Renamed", null, null));
        Assert.Equal("Draft Renamed", updatedDraft.Name);

        var published = await CreateAndPublishAsync(db, admin);
        var updatedPublished = await NewService(db, admin).UpdateAsync(published.Id, new UpdateProcessDefinitionRequest("Published Renamed", null, null));
        Assert.Equal("Published Renamed", updatedPublished.Name);
    }

    [Fact]
    public async Task CreateVersion_WhileArchived_ThrowsConflict()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        await NewService(db).ArchiveAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));

        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewService(db, admin).CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(MinimalDefinition())));
        Assert.Equal("PROCESS_DEFINITION_NOT_EDITABLE", ex.Code);
    }

    // ---- ChangeReason ----

    [Fact]
    public async Task Publish_WithChangeReason_PersistsAndIsImmutable()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin, changeReason: "Initial rollout");

        var versions = await NewService(db).GetVersionsAsync(definition.Id);
        var published = versions.Single(v => v.Status == ProcessVersionStatus.Published);
        Assert.Equal("Initial rollout", published.ChangeReason);
    }

    [Fact]
    public async Task Publish_ChangeReasonTooLong_ThrowsBadRequest()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await NewService(db, admin).CreateAsync(new CreateProcessDefinitionRequest($"gov-{Guid.NewGuid():N}", "Test", null, null));
        await NewService(db, admin).CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(MinimalDefinition()));

        var tooLong = new string('x', 1001);
        var ex = await Assert.ThrowsAsync<BadRequestAppException>(() =>
            NewEngine(db).PublishVersionAsync(definition.Id, admin, tooLong));
        Assert.Equal("CHANGE_REASON_TOO_LONG", ex.Code);
    }

    // ---- Historical immutability ----

    [Fact]
    public async Task HistoricalProcessInstance_KeepsOriginalVersion_AfterSuspendArchiveRestore()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var key = GetKey(db, definition.Id);
        var instanceA = await NewEngine(db).StartProcessAsync(new StartProcessRequest(key, null), admin);

        var suspended = await NewService(db).SuspendAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));
        var archived = await NewService(db).ArchiveAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(suspended.RowVersion));
        var restored = await NewService(db).RestoreAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(archived.RowVersion));

        var reloadedInstance = await db.ProcessInstances.AsNoTracking().SingleAsync(p => p.Id == instanceA.Id);
        Assert.Equal(instanceA.ProcessVersionId, reloadedInstance.ProcessVersionId);
        Assert.Equal(restored.CurrentVersionId, reloadedInstance.ProcessVersionId); // still version 1, unchanged
    }

    [Fact]
    public async Task RunningInstance_ContinuesAfterSuspend_TaskRemainsActionableAndCompletes()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var key = GetKey(db, definition.Id);
        var instance = await NewEngine(db).StartProcessAsync(new StartProcessRequest(key, null), admin);

        await NewService(db).SuspendAsync(definition.Id, admin, new[] { AdministratorRole }, new ProcessLifecycleActionRequest(definition.RowVersion));

        var task = await db.TaskInstances.AsNoTracking().SingleAsync(t => t.ProcessInstanceId == instance.Id);
        var completed = await NewEngine(db).CompleteTaskAsync(task.Id, admin, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, completed.Status);

        var reloadedInstance = await db.ProcessInstances.AsNoTracking().SingleAsync(p => p.Id == instance.Id);
        Assert.Equal(ProcessInstanceStatus.Completed, reloadedInstance.Status);
    }

    // ---- Version Comparison ----

    [Fact]
    public async Task CompareVersions_DetectsAddedRemovedModifiedNodesAndTransitions()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definitionV1 = await CreateAndPublishAsync(db, admin, taskName: "Original Task Name");
        var v1Id = (await NewService(db).GetVersionsAsync(definitionV1.Id)).Single(v => v.Status == ProcessVersionStatus.Published).Id;

        var v2Graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("task", WorkflowNodeType.UserTask, "Renamed Task", new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, "")), // modified: Name
                new WorkflowNodeDefinition("review", WorkflowNodeType.UserTask, "New Review Step", new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, "")), // added
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "task"),
                new WorkflowTransitionDefinition("t3", "task", "review"), // added
                new WorkflowTransitionDefinition("t4", "review", "end"), // added
                // t2 (task -> end) removed
            });
        await NewService(db, admin).CreateVersionAsync(definitionV1.Id, new CreateProcessVersionRequest(v2Graph));
        var published2 = await NewEngine(db).PublishVersionAsync(definitionV1.Id, admin, "adds a review step");

        var diff = await NewService(db).CompareVersionsAsync(definitionV1.Id, v1Id, published2.Id);

        Assert.Single(diff.AddedNodes);
        Assert.Equal("review", diff.AddedNodes[0].NodeId);
        Assert.Empty(diff.RemovedNodes);
        Assert.Single(diff.ModifiedNodes);
        Assert.Equal("task", diff.ModifiedNodes[0].NodeId);
        Assert.Contains("Name", diff.ModifiedNodes[0].ChangedFields);

        Assert.Equal(2, diff.AddedTransitions.Count);
        Assert.Single(diff.RemovedTransitions);
        Assert.Equal("t2", diff.RemovedTransitions[0].TransitionId);
        Assert.NotEqual("No workflow-level changes detected.", diff.Summary);
    }

    [Fact]
    public async Task CompareVersions_IdenticalDefinitions_ReturnsEmptyDiff()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var v1Id = (await NewService(db).GetVersionsAsync(definition.Id)).Single(v => v.Status == ProcessVersionStatus.Published).Id;

        var diff = await NewService(db).CompareVersionsAsync(definition.Id, v1Id, v1Id);

        Assert.Empty(diff.AddedNodes);
        Assert.Empty(diff.RemovedNodes);
        Assert.Empty(diff.ModifiedNodes);
        Assert.Empty(diff.AddedTransitions);
        Assert.Empty(diff.RemovedTransitions);
        Assert.Empty(diff.ModifiedTransitions);
        Assert.Equal("No workflow-level changes detected.", diff.Summary);
    }

    // Sequential approval assignment order is semantically meaningful — reordering the same two
    // candidates must be reported as a modification, never silently ignored as "the same set."
    [Fact]
    public async Task CompareVersions_ApprovalAssignmentOrderChange_IsDetectedAsModification()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var approverA = await CreateUserAsync(db);
        var approverB = await CreateUserAsync(db);

        WorkflowDefinition ApprovalGraph(Guid first, Guid second) => new(
            Nodes: new WorkflowNodeDefinition[]
            {
                new("start", WorkflowNodeType.Start, "Start"),
                new("approval", WorkflowNodeType.ApprovalTask, "Approval", Approval: new ApprovalConfig(ApprovalPolicy.Sequential, new[]
                {
                    new WorkflowAssignment(WorkflowAssignmentType.User, first.ToString()),
                    new WorkflowAssignment(WorkflowAssignmentType.User, second.ToString()),
                })),
                new("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[] { new WorkflowTransitionDefinition("t1", "start", "approval"), new WorkflowTransitionDefinition("t2", "approval", "end") });

        var definition = await NewService(db, admin).CreateAsync(new CreateProcessDefinitionRequest($"gov-{Guid.NewGuid():N}", "Sequential Order Test", null, null));
        await NewService(db, admin).CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(ApprovalGraph(approverA, approverB)));
        var v1 = await NewEngine(db).PublishVersionAsync(definition.Id, admin);

        await NewService(db, admin).CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(ApprovalGraph(approverB, approverA))); // order swapped
        var v2 = await NewEngine(db).PublishVersionAsync(definition.Id, admin);

        var diff = await NewService(db).CompareVersionsAsync(definition.Id, v1.Id, v2.Id);

        Assert.Single(diff.ModifiedNodes);
        Assert.Contains("ApprovalConfig", diff.ModifiedNodes[0].ChangedFields);
    }

    [Fact]
    public async Task CompareVersions_CrossDefinitionVersion_ThrowsNotFound()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definitionA = await CreateAndPublishAsync(db, admin);
        var definitionB = await CreateAndPublishAsync(db, admin);
        var versionOfA = (await NewService(db).GetVersionsAsync(definitionA.Id)).Single(v => v.Status == ProcessVersionStatus.Published).Id;
        var versionOfB = (await NewService(db).GetVersionsAsync(definitionB.Id)).Single(v => v.Status == ProcessVersionStatus.Published).Id;

        var ex = await Assert.ThrowsAsync<NotFoundAppException>(() =>
            NewService(db).CompareVersionsAsync(definitionA.Id, versionOfA, versionOfB));
        Assert.Equal("PROCESS_VERSION_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task CompareVersions_MissingVersion_ThrowsNotFound()
    {
        await using var db = PostgresFixture.CreateContext();
        var admin = await CreateUserAsync(db);
        var definition = await CreateAndPublishAsync(db, admin);
        var v1Id = (await NewService(db).GetVersionsAsync(definition.Id)).Single(v => v.Status == ProcessVersionStatus.Published).Id;

        var ex = await Assert.ThrowsAsync<NotFoundAppException>(() =>
            NewService(db).CompareVersionsAsync(definition.Id, v1Id, Guid.NewGuid()));
        Assert.Equal("PROCESS_VERSION_NOT_FOUND", ex.Code);
    }

    private static string GetKey(BpmDbContext db, Guid processDefinitionId) =>
        db.ProcessDefinitions.AsNoTracking().Single(p => p.Id == processDefinitionId).Key;

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
