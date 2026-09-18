using BPM.Application.Common;
using BPM.Application.Forms;
using BPM.Application.Processes;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Forms;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Engine = BPM.Workflow.Engine;

namespace BPM.Tests.Workflow;

// Phase 10 MUST HAVE #3 — Form Version Pinning. Architecture Discovery found that a published
// ProcessVersion referenced a form only by FormDefinitionKey, re-resolving FormDefinition
// .CurrentVersionId live at every task-creation, rather than snapshotting a specific FormVersion
// the way ProcessVersion itself is fully immutable — so the exact same ProcessVersion could bind
// different tasks to different FormVersions if the form was republished mid-flight. This file
// proves the fix: WorkflowEngine.PublishVersionAsync now snapshots FormVersionId into each node's
// FormReference at publish time (see FormReference's own doc comment), and every task created for
// that node afterward — across every ProcessInstance on that version, and every Return re-entry —
// resolves that exact, frozen FormVersion.
[Collection("Postgres")]
public class FormVersionPinningTests
{
    private static ProcessDefinitionService NewProcessDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static FormDefinitionService NewFormDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateFormDefinitionRequestValidator(), new CreateFormVersionRequestValidator(), new UpdateFormDefinitionRequestValidator(), new UpdateFormVersionRequestValidator());

    private static Engine.WorkflowEngine NewWorkflowEngine(BpmDbContext db) => new(db);
    private static Engine.FormEngine NewFormEngine(BpmDbContext db) => new(db, new FormAuthorizationService(db));

    // No required fields — every Submit in this file can complete with zero saved data, keeping
    // the scenario focused purely on version-pinning, not form validation.
    private static FormSchema MinimalSchema() => new(new[]
    {
        new FormFieldDefinition("note", FormFieldType.Text, "Note"),
    });

    private static async Task<FormDefinitionDto> CreateAndPublishFormAsync(string key)
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewFormDefinitionService(setupDb);
        var definition = await service.CreateAsync(new CreateFormDefinitionRequest(key, "Pinning Test Form", null, null));
        await service.CreateVersionAsync(definition.Id, new CreateFormVersionRequest(MinimalSchema()));

        await using var publishDb = PostgresFixture.CreateContext();
        var published = await NewFormEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        await using var verifyDb = PostgresFixture.CreateContext();
        var currentVersionId = await verifyDb.FormDefinitions.Where(f => f.Id == definition.Id).Select(f => f.CurrentVersionId).SingleAsync();
        Assert.Equal(published.Id, currentVersionId);

        return definition;
    }

    // start -> applicant (UserTask, ProcessInitiator, bound to the given form key) ->
    // approval (ApprovalTask, AnyOne, Return enabled -> goes back to applicant) -> end.
    private static WorkflowDefinition GraphWithReturnableFormNode(string formKey, string approverRole) => new(
        Nodes: new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
            new WorkflowNodeDefinition("applicant", WorkflowNodeType.UserTask, "Applicant", Assignment: new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, ""), Form: new FormReference(formKey)),
            new WorkflowNodeDefinition("approval", WorkflowNodeType.ApprovalTask, "Approval", Approval: new ApprovalConfig(ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, approverRole) }, ReturnPolicy: new ReturnPolicy(Enabled: true))),
            new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
        },
        Transitions: new[]
        {
            new WorkflowTransitionDefinition("t1", "start", "applicant"),
            new WorkflowTransitionDefinition("t2", "applicant", "approval"),
            new WorkflowTransitionDefinition("t3", "approval", "end"),
        });

    private static async Task<(ProcessDefinitionDto Definition, Guid VersionId)> CreateAndPublishProcessAsync(WorkflowDefinition graph, string? key = null)
    {
        key ??= $"pin-process-{Guid.NewGuid():N}";
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewProcessDefinitionService(setupDb);
        var definition = await service.CreateAsync(new CreateProcessDefinitionRequest(key, "Pinning Test Process", null, null));
        await service.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        var published = await NewWorkflowEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        return (definition, published.Id);
    }

    private static async Task<Guid> CreateRoleWithMemberAsync(string roleName, Guid memberId)
    {
        await using var db = PostgresFixture.CreateContext();
        db.Users.Add(new User { Id = memberId, Username = $"user-{Guid.NewGuid():N}", DisplayName = "Role Member", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true });
        var role = new Role { Name = roleName };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.UserRoles.Add(new UserRole { UserId = memberId, RoleId = role.Id });
        await db.SaveChangesAsync();
        return role.Id;
    }

    private static async Task<Guid> FormInstanceVersionIdForProcessInstanceAsync(Guid processInstanceId)
    {
        await using var db = PostgresFixture.CreateContext();
        return await db.FormInstances.Where(f => f.ProcessInstanceId == processInstanceId).Select(f => f.FormVersionId).SingleAsync();
    }

    [Fact]
    public async Task PublishVersion_SnapshotsCurrentFormVersionId_IntoTheGraph()
    {
        var form = await CreateAndPublishFormAsync($"form-{Guid.NewGuid():N}");
        var formV1Id = await GetFormCurrentVersionIdAsync(form.Id);

        var (_, versionId) = await CreateAndPublishProcessAsync(GraphWithReturnableFormNode(form.Key, "Reviewer"));

        await using var db = PostgresFixture.CreateContext();
        var version = await db.ProcessVersions.SingleAsync(v => v.Id == versionId);
        var graph = WorkflowJson.TryDeserialize(version.DefinitionJson)!;
        var applicantNode = graph.Nodes.Single(n => n.Id == "applicant");

        Assert.Equal(formV1Id, applicantNode.Form!.FormVersionId);
    }

    [Fact]
    public async Task TwoInstancesOnTheSamePinnedProcessVersion_BothUseTheFormVersionPinnedAtPublish_EvenAfterARepublish()
    {
        var formKey = $"form-{Guid.NewGuid():N}";
        var form = await CreateAndPublishFormAsync(formKey);
        var formV1Id = await GetFormCurrentVersionIdAsync(form.Id);

        var (process, _) = await CreateAndPublishProcessAsync(GraphWithReturnableFormNode(formKey, "Reviewer"));

        // Instance A starts while Form V1 is current.
        await using var startADb = PostgresFixture.CreateContext();
        var instanceA = await NewWorkflowEngine(startADb).StartProcessAsync(new StartProcessRequest(process.Key, null), Guid.NewGuid());
        Assert.Equal(formV1Id, await FormInstanceVersionIdForProcessInstanceAsync(instanceA.Id));

        // Republish the form to V2 — this must NOT retroactively or prospectively change what
        // ProcessVersion P1 resolves, since P1 already pinned V1 at its own publish time.
        await using var newVersionDb = PostgresFixture.CreateContext();
        await NewFormDefinitionService(newVersionDb).CreateVersionAsync(form.Id, new CreateFormVersionRequest(MinimalSchema()));
        await using var publishV2Db = PostgresFixture.CreateContext();
        var formV2 = await NewFormEngine(publishV2Db).PublishVersionAsync(form.Id, Guid.NewGuid());
        Assert.NotEqual(formV1Id, formV2.Id);

        // Instance B starts AFTER the republish, but on the SAME already-published ProcessVersion
        // (no new ProcessVersion was published) — it must still resolve V1, deterministically.
        await using var startBDb = PostgresFixture.CreateContext();
        var instanceB = await NewWorkflowEngine(startBDb).StartProcessAsync(new StartProcessRequest(process.Key, null), Guid.NewGuid());
        Assert.Equal(formV1Id, await FormInstanceVersionIdForProcessInstanceAsync(instanceB.Id));

        // Instance A's original FormInstance remains untouched.
        Assert.Equal(formV1Id, await FormInstanceVersionIdForProcessInstanceAsync(instanceA.Id));
    }

    [Fact]
    public async Task ReturnIntoTheFormNode_AfterARepublish_StillUsesThePinnedFormVersion()
    {
        var formKey = $"form-{Guid.NewGuid():N}";
        var form = await CreateAndPublishFormAsync(formKey);
        var formV1Id = await GetFormCurrentVersionIdAsync(form.Id);

        var approverRole = $"Reviewer-{Guid.NewGuid():N}";
        var reviewer = Guid.NewGuid();
        await CreateRoleWithMemberAsync(approverRole, reviewer);
        var (process, _) = await CreateAndPublishProcessAsync(GraphWithReturnableFormNode(formKey, approverRole));

        var applicant = Guid.NewGuid();
        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewWorkflowEngine(startDb).StartProcessAsync(new StartProcessRequest(process.Key, null), applicant);

        // Submit the applicant's form to advance into the Approval node.
        await using var loadDb1 = PostgresFixture.CreateContext();
        var firstFormInstance = await loadDb1.FormInstances.SingleAsync(f => f.ProcessInstanceId == instance.Id);
        Assert.Equal(formV1Id, firstFormInstance.FormVersionId);

        await using var submitDb = PostgresFixture.CreateContext();
        await NewFormEngine(submitDb).SubmitAsync(firstFormInstance.Id, applicant, Array.Empty<string>());

        // Republish the form to V2 while the Approval task is pending.
        await using var newVersionDb = PostgresFixture.CreateContext();
        await NewFormDefinitionService(newVersionDb).CreateVersionAsync(form.Id, new CreateFormVersionRequest(MinimalSchema()));
        await using var publishV2Db = PostgresFixture.CreateContext();
        var formV2 = await NewFormEngine(publishV2Db).PublishVersionAsync(form.Id, Guid.NewGuid());
        Assert.NotEqual(formV1Id, formV2.Id);

        // Return the Approval task back to the applicant.
        await using var loadDb2 = PostgresFixture.CreateContext();
        var approvalTask = await loadDb2.TaskInstances.SingleAsync(t => t.ProcessInstanceId == instance.Id && t.NodeId == "approval");

        await using var returnDb = PostgresFixture.CreateContext();
        await NewWorkflowEngine(returnDb).ReturnTaskAsync(approvalTask.Id, reviewer, new[] { approverRole });

        // The newly-created applicant task's FormInstance must still resolve V1, the version
        // pinned into this ProcessVersion at its own publish time — never the now-current V2.
        await using var verifyDb = PostgresFixture.CreateContext();
        var newApplicantTask = await verifyDb.TaskInstances
            .Where(t => t.ProcessInstanceId == instance.Id && t.NodeId == "applicant")
            .OrderByDescending(t => t.CreatedAt)
            .FirstAsync();
        var returnedFormInstance = await verifyDb.FormInstances.SingleAsync(f => f.TaskInstanceId == newApplicantTask.Id);
        Assert.Equal(formV1Id, returnedFormInstance.FormVersionId);
    }

    [Fact]
    public async Task ANewerProcessVersion_MayIntentionallyPinTheNewerFormVersion()
    {
        var formKey = $"form-{Guid.NewGuid():N}";
        var form = await CreateAndPublishFormAsync(formKey);
        var formV1Id = await GetFormCurrentVersionIdAsync(form.Id);

        var (process, p1VersionId) = await CreateAndPublishProcessAsync(GraphWithReturnableFormNode(formKey, "Reviewer"));

        // Republish the form to V2.
        await using var newFormVersionDb = PostgresFixture.CreateContext();
        await NewFormDefinitionService(newFormVersionDb).CreateVersionAsync(form.Id, new CreateFormVersionRequest(MinimalSchema()));
        await using var publishV2Db = PostgresFixture.CreateContext();
        var formV2 = await NewFormEngine(publishV2Db).PublishVersionAsync(form.Id, Guid.NewGuid());

        // Publish a brand-new ProcessVersion (P2) of the same process, referencing the same form
        // key — this is a genuinely new publish action, so it's allowed to (and does) pick up
        // whatever is current at that moment: Form V2.
        await using var newProcessVersionDb = PostgresFixture.CreateContext();
        var processDefinitionId = await newProcessVersionDb.ProcessDefinitions.Where(p => p.Key == process.Key).Select(p => p.Id).SingleAsync();
        await NewProcessDefinitionService(newProcessVersionDb).CreateVersionAsync(processDefinitionId, new CreateProcessVersionRequest(GraphWithReturnableFormNode(formKey, "Reviewer")));

        await using var publishP2Db = PostgresFixture.CreateContext();
        var p2 = await NewWorkflowEngine(publishP2Db).PublishVersionAsync(processDefinitionId, Guid.NewGuid());

        var p2ApplicantNode = p2.Definition.Nodes.Single(n => n.Id == "applicant");
        Assert.Equal(formV2.Id, p2ApplicantNode.Form!.FormVersionId);

        // A new instance on P2 (now the definition's current version) resolves Form V2.
        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewWorkflowEngine(startDb).StartProcessAsync(new StartProcessRequest(process.Key, null), Guid.NewGuid());
        Assert.Equal(formV2.Id, await FormInstanceVersionIdForProcessInstanceAsync(instance.Id));

        // P1 (the original ProcessVersion) remains immutable and still pins V1.
        await using var p1VerifyDb = PostgresFixture.CreateContext();
        var p1 = await p1VerifyDb.ProcessVersions.SingleAsync(v => v.Id == p1VersionId);
        var p1Graph = WorkflowJson.TryDeserialize(p1.DefinitionJson)!;
        Assert.Equal(formV1Id, p1Graph.Nodes.Single(n => n.Id == "applicant").Form!.FormVersionId);
    }

    private static async Task<Guid> GetFormCurrentVersionIdAsync(Guid formDefinitionId)
    {
        await using var db = PostgresFixture.CreateContext();
        return (await db.FormDefinitions.Where(f => f.Id == formDefinitionId).Select(f => f.CurrentVersionId).SingleAsync())!.Value;
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
