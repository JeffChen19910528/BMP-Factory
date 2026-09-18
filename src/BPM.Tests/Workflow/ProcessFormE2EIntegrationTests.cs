using System.Text.Json;
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

// Phase 5.4.4 — Process + Form E2E Integration & Hardening. This file exercises exactly the
// ground no single earlier phase's tests covered: a Form bound to a UserTask, flowing through a
// *multi-step approval chain* (Applicant -> Manager -> Finance), against the real
// WorkflowEngine/ApprovalEngine/FormEngine and a real Postgres database — not a re-test of
// Sequential/AnyOne/Reject/Return/Delegate/Transfer/AddApprover semantics themselves, which
// ApprovalEngineIntegrationTests already covers exhaustively (per this phase's own "do not
// duplicate tests that already exist" instruction).
[Collection("Postgres")]
public class ProcessFormE2EIntegrationTests
{
    private static ProcessDefinitionService NewProcessDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static FormDefinitionService NewFormDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateFormDefinitionRequestValidator(), new CreateFormVersionRequestValidator(), new UpdateFormDefinitionRequestValidator(), new UpdateFormVersionRequestValidator());

    private static Engine.WorkflowEngine NewWorkflowEngine(BpmDbContext db) => new(db);
    private static Engine.FormEngine NewFormEngine(BpmDbContext db) => new(db, new FormAuthorizationService(db));
    private static TaskQueryService NewTaskQueryService(BpmDbContext db) => new(db);

    private static async Task<Guid> CreateUserAsync(BpmDbContext db, string? displayName = null)
    {
        var user = new User { Username = $"user-{Guid.NewGuid():N}", DisplayName = displayName ?? "Test User", Email = $"{Guid.NewGuid():N}@bpm-tests.local", IsActive = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<string> CreateRoleWithMembersAsync(BpmDbContext db, params Guid[] userIds)
    {
        var roleName = $"Role-{Guid.NewGuid():N}";
        db.Roles.Add(new Role { Name = roleName });
        await db.SaveChangesAsync();
        foreach (var userId in userIds)
        {
            db.UserRoles.Add(new UserRole { UserId = userId, RoleId = (await db.Roles.SingleAsync(r => r.Name == roleName)).Id });
        }
        await db.SaveChangesAsync();
        return roleName;
    }

    // Expense Request form: Applicant, Department (unused field, included per §2's field-type
    // coverage), Expense Type, Amount, Description (conditionally required when Expense Type =
    // OTHER), Quantity/UnitPrice/Total (Total is Calculated), Attachment.
    private static FormSchema ExpenseFormSchema() => new(
        new[]
        {
            new FormFieldDefinition("expenseType", FormFieldType.Select, "Expense Type", Required: true, Options: new[] { new FormFieldOption("TRAVEL", "Travel"), new FormFieldOption("OTHER", "Other") }),
            new FormFieldDefinition("description", FormFieldType.Text, "Description"),
            new FormFieldDefinition("department", FormFieldType.Department, "Department"),
            new FormFieldDefinition("quantity", FormFieldType.Number, "Quantity", Required: true),
            new FormFieldDefinition("unitPrice", FormFieldType.Currency, "Unit Price", Required: true),
            new FormFieldDefinition("total", FormFieldType.Currency, "Total"),
            new FormFieldDefinition("attachment", FormFieldType.File, "Receipt"),
        },
        new[]
        {
            new FormRule("r1", "description", FormRuleType.Required, new FormFieldCondition("expenseType", FormConditionOperator.Equals, "OTHER")),
            new FormRule("r2", "total", FormRuleType.Calculated, Formula: "quantity * unitPrice"),
        });

    private static async Task<FormDefinitionDto> CreateAndPublishFormAsync(BpmDbContext dbFactory_unused, FormSchema schema)
    {
        var key = $"expense-form-{Guid.NewGuid():N}";
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewFormDefinitionService(setupDb);
        var definition = await service.CreateAsync(new CreateFormDefinitionRequest(key, "Expense Request Form", null, null));
        await service.CreateVersionAsync(definition.Id, new CreateFormVersionRequest(schema));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewFormEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        return definition;
    }

    // Applicant (UserTask, ProcessInitiator, bound to the Expense form) -> Manager (ApprovalTask,
    // Role) -> Finance (ApprovalTask, Role) -> End.
    private static WorkflowDefinition ExpenseProcessGraph(string formKey, string managerRole, string financeRole) => new(
        Nodes: new[]
        {
            new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
            new WorkflowNodeDefinition("applicant", WorkflowNodeType.UserTask, "Applicant", Assignment: new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, ""), Form: new FormReference(formKey)),
            new WorkflowNodeDefinition("manager", WorkflowNodeType.ApprovalTask, "Manager Approval", Approval: new ApprovalConfig(ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, managerRole) })),
            new WorkflowNodeDefinition("finance", WorkflowNodeType.ApprovalTask, "Finance Approval", Approval: new ApprovalConfig(ApprovalPolicy.AnyOne, new[] { new WorkflowAssignment(WorkflowAssignmentType.Role, financeRole) })),
            new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
        },
        Transitions: new[]
        {
            new WorkflowTransitionDefinition("t1", "start", "applicant"),
            new WorkflowTransitionDefinition("t2", "applicant", "manager"),
            new WorkflowTransitionDefinition("t3", "manager", "finance"),
            new WorkflowTransitionDefinition("t4", "finance", "end"),
        });

    private static async Task<(ProcessDefinitionDto Process, string ProcessKey)> CreateAndPublishProcessAsync(WorkflowDefinition graph)
    {
        var key = $"expense-process-{Guid.NewGuid():N}";
        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewProcessDefinitionService(setupDb);
        var definition = await service.CreateAsync(new CreateProcessDefinitionRequest(key, "Expense Request Process", null, null));
        await service.CreateVersionAsync(definition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewWorkflowEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        return (definition, key);
    }

    [Fact]
    public async Task FullExpenseFlow_ApplicantSubmitsForm_ManagerApproves_FinanceApproves_ProcessCompletes()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var manager = await CreateUserAsync(setupDb, "Manager");
        var finance = await CreateUserAsync(setupDb, "Finance");
        var managerRole = await CreateRoleWithMembersAsync(setupDb, manager);
        var financeRole = await CreateRoleWithMembersAsync(setupDb, finance);

        var form = await CreateAndPublishFormAsync(setupDb, ExpenseFormSchema());
        var (process, processKey) = await CreateAndPublishProcessAsync(ExpenseProcessGraph(form.Key, managerRole, financeRole));

        var applicant = await CreateUserAsync(setupDb, "Applicant");

        // Start the process — the applicant is both initiator and the assignee of the first task.
        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewWorkflowEngine(startDb).StartProcessAsync(new StartProcessRequest(processKey, null), applicant);
        Assert.Equal(ProcessInstanceStatus.Running, instance.Status);

        await using var taskDb = PostgresFixture.CreateContext();
        var applicantTasks = await NewTaskQueryService(taskDb).GetMyTasksAsync(applicant, Array.Empty<string>(), new MyTasksQuery());
        var applicantTask = Assert.Single(applicantTasks.Items);
        Assert.Equal("applicant", applicantTask.NodeId);
        Assert.Equal(applicant, applicantTask.AssigneeId);

        // Resolve the task's bound FormInstance the same way TaskDetailPage.tsx does.
        await using var formInstancesDb = PostgresFixture.CreateContext();
        var formInstances = await new FormInstanceQueryService(formInstancesDb, new FormAuthorizationService(formInstancesDb)).GetByProcessInstanceAsync(instance.Id, applicant, Array.Empty<string>());
        var formInstance = Assert.Single(formInstances, f => f.TaskInstanceId == applicantTask.Id);
        Assert.Equal(FormInstanceStatus.Draft, formInstance.Status);

        // Save a draft: Expense Type = OTHER makes Description required; quantity/unitPrice feed
        // the Calculated total.
        await using var readDb = PostgresFixture.CreateContext();
        var initialData = await new FormInstanceQueryService(readDb, new FormAuthorizationService(readDb)).GetDataAsync(formInstance.Id, applicant, Array.Empty<string>());

        await using var saveDb = PostgresFixture.CreateContext();
        var draftPayload = JsonSerializer.Deserialize<JsonElement>("""{"expenseType":"OTHER","quantity":3,"unitPrice":100}""");
        var saved = await NewFormEngine(saveDb).SaveDataAsync(formInstance.Id, new UpdateFormDataRequest(draftPayload, initialData!.Version), applicant, Array.Empty<string>());
        Assert.Equal(300m, saved.Data.GetProperty("total").GetDecimal());

        // Submit without Description — conditionally required, rejected authoritatively.
        await using var submitMissingDb = PostgresFixture.CreateContext();
        var missingDescriptionEx = await Assert.ThrowsAsync<ValidationAppException>(() => NewFormEngine(submitMissingDb).SubmitAsync(formInstance.Id, applicant, Array.Empty<string>()));
        Assert.Contains(missingDescriptionEx.Errors, e => e.Code == "FIELD_REQUIRED");

        // Fill in Description and submit for real — this both completes the applicant's task and
        // creates the Manager's ApprovalTask, purely via FormEngine.SubmitAsync -> WorkflowTransitions.
        await using var descDb = PostgresFixture.CreateContext();
        var reread = await new FormInstanceQueryService(descDb, new FormAuthorizationService(descDb)).GetDataAsync(formInstance.Id, applicant, Array.Empty<string>());
        await using var finalSaveDb = PostgresFixture.CreateContext();
        var descPayload = JsonSerializer.Deserialize<JsonElement>("""{"description":"Client dinner"}""");
        var finalSave = await NewFormEngine(finalSaveDb).SaveDataAsync(formInstance.Id, new UpdateFormDataRequest(descPayload, reread!.Version), applicant, Array.Empty<string>());

        await using var submitDb = PostgresFixture.CreateContext();
        var submitted = await NewFormEngine(submitDb).SubmitAsync(formInstance.Id, applicant, Array.Empty<string>());
        Assert.Equal(FormInstanceStatus.Locked, submitted.Status);

        await using var afterSubmitTaskDb = PostgresFixture.CreateContext();
        var applicantTaskAfter = await new TaskQueryService(afterSubmitTaskDb).GetByIdAsync(applicantTask.Id, applicant, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, applicantTaskAfter!.Status);

        // Manager's ApprovalTask now exists.
        await using var managerTaskDb = PostgresFixture.CreateContext();
        var managerTasks = await NewTaskQueryService(managerTaskDb).GetMyTasksAsync(manager, new[] { managerRole }, new MyTasksQuery());
        var managerTask = Assert.Single(managerTasks.Items);
        Assert.Equal("manager", managerTask.NodeId);
        Assert.NotNull(managerTask.Approval);

        // Manager approves -> Finance's ApprovalTask is created.
        await using var approveDb = PostgresFixture.CreateContext();
        await NewWorkflowEngine(approveDb).ApproveTaskAsync(managerTask.Id, manager, new[] { managerRole });

        await using var financeTaskDb = PostgresFixture.CreateContext();
        var financeTasks = await NewTaskQueryService(financeTaskDb).GetMyTasksAsync(finance, new[] { financeRole }, new MyTasksQuery());
        var financeTask = Assert.Single(financeTasks.Items);
        Assert.Equal("finance", financeTask.NodeId);

        // Finance approves -> the process reaches End and completes.
        await using var financeApproveDb = PostgresFixture.CreateContext();
        await NewWorkflowEngine(financeApproveDb).ApproveTaskAsync(financeTask.Id, finance, new[] { financeRole });

        await using var verifyDb = PostgresFixture.CreateContext();
        var finalInstance = await verifyDb.ProcessInstances.SingleAsync(p => p.Id == instance.Id);
        Assert.Equal(ProcessInstanceStatus.Completed, finalInstance.Status);

        // Audit trail exists for the key events (§31) — reuses the existing AuditLog mechanism,
        // never a parallel one.
        var auditActions = await verifyDb.AuditLogs.Where(a => a.EntityId == instance.Id.ToString() || a.EntityId == formInstance.Id.ToString()).Select(a => a.Action).ToListAsync();
        Assert.Contains(AuditActions.StartProcess, auditActions);
    }

    [Fact]
    public async Task FormInstanceIDOR_UnrelatedUser_CannotViewSaveOrSubmitApplicantsForm()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var manager = await CreateUserAsync(setupDb);
        var managerRole = await CreateRoleWithMembersAsync(setupDb, manager);
        var form = await CreateAndPublishFormAsync(setupDb, ExpenseFormSchema());
        var (_, processKey) = await CreateAndPublishProcessAsync(ExpenseProcessGraph(form.Key, managerRole, managerRole));

        var applicant = await CreateUserAsync(setupDb);
        var stranger = await CreateUserAsync(setupDb);

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewWorkflowEngine(startDb).StartProcessAsync(new StartProcessRequest(processKey, null), applicant);

        await using var resolveDb = PostgresFixture.CreateContext();
        var formInstances = await new FormInstanceQueryService(resolveDb, new FormAuthorizationService(resolveDb)).GetByProcessInstanceAsync(instance.Id, applicant, Array.Empty<string>());
        var formInstanceId = formInstances.Single().Id;

        // The stranger's own "list by process" view sees nothing (authorization-filtered, not
        // just hidden client-side).
        await using var strangerListDb = PostgresFixture.CreateContext();
        var strangerView = await new FormInstanceQueryService(strangerListDb, new FormAuthorizationService(strangerListDb)).GetByProcessInstanceAsync(instance.Id, stranger, Array.Empty<string>());
        Assert.Empty(strangerView);

        // Direct-by-id access is rejected outright, not silently empty.
        await using var strangerGetDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ForbiddenAppException>(() => new FormInstanceQueryService(strangerGetDb, new FormAuthorizationService(strangerGetDb)).GetByIdAsync(formInstanceId, stranger, Array.Empty<string>()));

        await using var strangerDataDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ForbiddenAppException>(() => new FormInstanceQueryService(strangerDataDb, new FormAuthorizationService(strangerDataDb)).GetDataAsync(formInstanceId, stranger, Array.Empty<string>()));

        var payload = JsonSerializer.Deserialize<JsonElement>("""{"quantity":1}""");
        await using var strangerSaveDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ForbiddenAppException>(() => NewFormEngine(strangerSaveDb).SaveDataAsync(formInstanceId, new UpdateFormDataRequest(payload, "AAAAAAAAAAE="), stranger, Array.Empty<string>()));

        await using var strangerSubmitDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ForbiddenAppException>(() => NewFormEngine(strangerSubmitDb).SubmitAsync(formInstanceId, stranger, Array.Empty<string>()));
    }

    [Fact]
    public async Task TaskIDOR_UnrelatedUser_CannotViewOrActOnManagersApprovalTask()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var manager = await CreateUserAsync(setupDb);
        var managerRole = await CreateRoleWithMembersAsync(setupDb, manager);
        var form = await CreateAndPublishFormAsync(setupDb, ExpenseFormSchema());
        var (_, processKey) = await CreateAndPublishProcessAsync(ExpenseProcessGraph(form.Key, managerRole, managerRole));

        var applicant = await CreateUserAsync(setupDb);
        var stranger = await CreateUserAsync(setupDb);

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewWorkflowEngine(startDb).StartProcessAsync(new StartProcessRequest(processKey, null), applicant);

        // Advance straight to the Manager ApprovalTask by submitting the applicant's form.
        await using var resolveDb = PostgresFixture.CreateContext();
        var applicantTask = (await NewTaskQueryService(resolveDb).GetMyTasksAsync(applicant, Array.Empty<string>(), new MyTasksQuery())).Items.Single();
        var formInstanceId = (await new FormInstanceQueryService(resolveDb, new FormAuthorizationService(resolveDb)).GetByProcessInstanceAsync(instance.Id, applicant, Array.Empty<string>())).Single().Id;
        var data = await new FormInstanceQueryService(resolveDb, new FormAuthorizationService(resolveDb)).GetDataAsync(formInstanceId, applicant, Array.Empty<string>());

        await using var saveDb = PostgresFixture.CreateContext();
        var payload = JsonSerializer.Deserialize<JsonElement>("""{"expenseType":"TRAVEL","quantity":1,"unitPrice":10}""");
        var saved = await NewFormEngine(saveDb).SaveDataAsync(formInstanceId, new UpdateFormDataRequest(payload, data!.Version), applicant, Array.Empty<string>());
        await using var submitDb = PostgresFixture.CreateContext();
        await NewFormEngine(submitDb).SubmitAsync(formInstanceId, applicant, Array.Empty<string>());

        await using var managerTaskDb = PostgresFixture.CreateContext();
        var managerTask = (await new TaskQueryService(managerTaskDb).GetMyTasksAsync(manager, new[] { managerRole }, new MyTasksQuery())).Items.Single();

        // The stranger holds no relevant role and isn't the assignee — every read/write surface
        // must reject them.
        await using var strangerGetDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ForbiddenAppException>(() => new TaskQueryService(strangerGetDb).GetByIdAsync(managerTask.Id, stranger, Array.Empty<string>()));

        await using var strangerApproveDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ForbiddenAppException>(() => NewWorkflowEngine(strangerApproveDb).ApproveTaskAsync(managerTask.Id, stranger, Array.Empty<string>()));

        await using var strangerRejectDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ForbiddenAppException>(() => NewWorkflowEngine(strangerRejectDb).RejectTaskAsync(managerTask.Id, stranger, Array.Empty<string>()));

        await using var strangerDelegateDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ForbiddenAppException>(() => NewWorkflowEngine(strangerDelegateDb).DelegateTaskAsync(managerTask.Id, stranger, manager, CancellationToken.None));

        await using var strangerTransferDb = PostgresFixture.CreateContext();
        await Assert.ThrowsAsync<ForbiddenAppException>(() => NewWorkflowEngine(strangerTransferDb).TransferTaskAsync(managerTask.Id, stranger, Array.Empty<string>(), manager, null, CancellationToken.None));
    }

    [Fact]
    public async Task RunningProcessInstance_RetainsOriginalFormVersion_AfterANewerVersionIsPublished()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var applicant = await CreateUserAsync(setupDb);
        var managerRole = await CreateRoleWithMembersAsync(setupDb, applicant);

        var formKey = $"expense-form-{Guid.NewGuid():N}";
        var formService = NewFormDefinitionService(setupDb);
        var formDefinition = await formService.CreateAsync(new CreateFormDefinitionRequest(formKey, "Versioned Form", null, null));
        var v1Schema = new FormSchema(new[] { new FormFieldDefinition("note", FormFieldType.Text, "Note V1") });
        await formService.CreateVersionAsync(formDefinition.Id, new CreateFormVersionRequest(v1Schema));
        await using var publishV1Db = PostgresFixture.CreateContext();
        await NewFormEngine(publishV1Db).PublishVersionAsync(formDefinition.Id, Guid.NewGuid());

        var (_, processKey) = await CreateAndPublishProcessAsync(ExpenseProcessGraph(formKey, managerRole, managerRole));

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewWorkflowEngine(startDb).StartProcessAsync(new StartProcessRequest(processKey, null), applicant);

        await using var resolveDb = PostgresFixture.CreateContext();
        var formInstance = (await new FormInstanceQueryService(resolveDb, new FormAuthorizationService(resolveDb)).GetByProcessInstanceAsync(instance.Id, applicant, Array.Empty<string>())).Single();
        var v1Id = formInstance.FormVersionId;

        // Publish a v2 with a different schema — the already-running instance must not silently
        // move to it.
        await using var v2Db = PostgresFixture.CreateContext();
        var v2Service = NewFormDefinitionService(v2Db);
        var v2Schema = new FormSchema(new[] { new FormFieldDefinition("note", FormFieldType.Text, "Note V2"), new FormFieldDefinition("extra", FormFieldType.Text, "Extra V2") });
        await v2Service.CreateVersionAsync(formDefinition.Id, new CreateFormVersionRequest(v2Schema));
        await using var publishV2Db = PostgresFixture.CreateContext();
        await NewFormEngine(publishV2Db).PublishVersionAsync(formDefinition.Id, Guid.NewGuid());

        await using var verifyDb = PostgresFixture.CreateContext();
        var formInstanceAfter = (await new FormInstanceQueryService(verifyDb, new FormAuthorizationService(verifyDb)).GetByProcessInstanceAsync(instance.Id, applicant, Array.Empty<string>())).Single();
        Assert.Equal(v1Id, formInstanceAfter.FormVersionId);

        var dataQuery = await new FormInstanceQueryService(verifyDb, new FormAuthorizationService(verifyDb)).GetDataAsync(formInstanceAfter.Id, applicant, Array.Empty<string>());
        // Submitting against v1's schema (only "note") must not require v2's "extra" field.
        await using var saveDb = PostgresFixture.CreateContext();
        var payload = JsonSerializer.Deserialize<JsonElement>("""{"note":"still v1"}""");
        await NewFormEngine(saveDb).SaveDataAsync(formInstanceAfter.Id, new UpdateFormDataRequest(payload, dataQuery!.Version), applicant, Array.Empty<string>());
        await using var submitDb = PostgresFixture.CreateContext();
        var submitted = await NewFormEngine(submitDb).SubmitAsync(formInstanceAfter.Id, applicant, Array.Empty<string>());
        Assert.Equal(FormInstanceStatus.Locked, submitted.Status);
    }

    [Fact]
    public async Task DuplicateSubmit_SecondAttempt_IsRejected_TaskNotCompletedTwice()
    {
        await using var setupDb = PostgresFixture.CreateContext();
        var applicant = await CreateUserAsync(setupDb);
        var managerRole = await CreateRoleWithMembersAsync(setupDb, applicant);
        var formKey = $"expense-form-{Guid.NewGuid():N}";
        var formService = NewFormDefinitionService(setupDb);
        var formDefinition = await formService.CreateAsync(new CreateFormDefinitionRequest(formKey, "Simple Form", null, null));
        await formService.CreateVersionAsync(formDefinition.Id, new CreateFormVersionRequest(new FormSchema(new[] { new FormFieldDefinition("note", FormFieldType.Text, "Note") })));
        await using var publishFormDb = PostgresFixture.CreateContext();
        await NewFormEngine(publishFormDb).PublishVersionAsync(formDefinition.Id, Guid.NewGuid());

        var (_, processKey) = await CreateAndPublishProcessAsync(ExpenseProcessGraph(formKey, managerRole, managerRole));

        await using var startDb = PostgresFixture.CreateContext();
        var instance = await NewWorkflowEngine(startDb).StartProcessAsync(new StartProcessRequest(processKey, null), applicant);

        await using var resolveDb = PostgresFixture.CreateContext();
        var formInstance = (await new FormInstanceQueryService(resolveDb, new FormAuthorizationService(resolveDb)).GetByProcessInstanceAsync(instance.Id, applicant, Array.Empty<string>())).Single();
        var data = await new FormInstanceQueryService(resolveDb, new FormAuthorizationService(resolveDb)).GetDataAsync(formInstance.Id, applicant, Array.Empty<string>());

        await using var saveDb = PostgresFixture.CreateContext();
        var payload = JsonSerializer.Deserialize<JsonElement>("""{"note":"hello"}""");
        await NewFormEngine(saveDb).SaveDataAsync(formInstance.Id, new UpdateFormDataRequest(payload, data!.Version), applicant, Array.Empty<string>());

        await using var firstSubmitDb = PostgresFixture.CreateContext();
        var firstSubmit = await NewFormEngine(firstSubmitDb).SubmitAsync(formInstance.Id, applicant, Array.Empty<string>());
        Assert.Equal(FormInstanceStatus.Locked, firstSubmit.Status);

        // A retried/duplicate Submit click must not create a second task-completion or a second
        // workflow transition — the instance is no longer Draft, so it fails closed.
        await using var secondSubmitDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() => NewFormEngine(secondSubmitDb).SubmitAsync(formInstance.Id, applicant, Array.Empty<string>()));
        Assert.Equal("FORM_NOT_EDITABLE", ex.Code);

        // Exactly one Manager ApprovalTask exists — not two.
        await using var verifyDb = PostgresFixture.CreateContext();
        var managerTaskCount = await verifyDb.TaskInstances.CountAsync(t => t.ProcessInstanceId == instance.Id && t.NodeId == "manager");
        Assert.Equal(1, managerTaskCount);
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
