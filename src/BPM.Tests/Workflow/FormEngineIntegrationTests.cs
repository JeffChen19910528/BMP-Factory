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

// Phase 4 Form Engine coverage, against the same real "bpm_test" Postgres database Phase 2/3 use
// (see PostgresFixture) — concurrency and transactional behavior are exactly the kind of thing a
// mock can't exercise honestly (Skill.md Phase 4 §31/§32).
[Collection("Postgres")]
public class FormEngineIntegrationTests
{
    private static FormDefinitionService NewFormDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateFormDefinitionRequestValidator(), new CreateFormVersionRequestValidator(), new UpdateFormDefinitionRequestValidator(), new UpdateFormVersionRequestValidator());

    private static Engine.FormEngine NewFormEngine(BpmDbContext db) => new(db, new FormAuthorizationService(db));

    private static ProcessDefinitionService NewProcessDefinitionService(BpmDbContext db) =>
        new(db, new AuditService(db, new FixedCurrentUser(Guid.Empty)), new FixedCurrentUser(Guid.Empty), new CreateProcessDefinitionRequestValidator(), new CreateProcessVersionRequestValidator(), new UpdateProcessVersionRequestValidator(), new UpdateProcessDefinitionRequestValidator());

    private static Engine.WorkflowEngine NewWorkflowEngine(BpmDbContext db) => new(db);

    private static FormSchema PurchaseRequestSchema() => new(new[]
    {
        new FormFieldDefinition("itemName", FormFieldType.Text, "Item Name", Required: true),
        new FormFieldDefinition("quantity", FormFieldType.Number, "Quantity", Required: true, Validation: new FormFieldValidation(MinValue: 1)),
        new FormFieldDefinition("amount", FormFieldType.Currency, "Amount", Required: true, Validation: new FormFieldValidation(MinValue: 0)),
    });

    private static async Task<FormDefinitionDto> CreateAndPublishFormAsync(FormSchema schema, string? key = null)
    {
        key ??= $"form-{Guid.NewGuid():N}";

        await using var setupDb = PostgresFixture.CreateContext();
        var service = NewFormDefinitionService(setupDb);
        var definition = await service.CreateAsync(new CreateFormDefinitionRequest(key, "Test Form", null, null));
        await service.CreateVersionAsync(definition.Id, new CreateFormVersionRequest(schema));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewFormEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid());

        return definition;
    }

    // ---- Definition / Version / Publish ----

    [Fact]
    public async Task CreateFormDefinition_DuplicateKey_Rejected()
    {
        var key = $"dup-{Guid.NewGuid():N}";
        await using var db = PostgresFixture.CreateContext();
        var service = NewFormDefinitionService(db);
        await service.CreateAsync(new CreateFormDefinitionRequest(key, "First", null, null));

        await using var db2 = PostgresFixture.CreateContext();
        var service2 = NewFormDefinitionService(db2);
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() => service2.CreateAsync(new CreateFormDefinitionRequest(key, "Second", null, null)));
        Assert.Equal("FORM_DEFINITION_KEY_TAKEN", ex.Code);
    }

    [Fact]
    public async Task PublishVersion_InvalidSchema_RejectedAndNotPublished()
    {
        var invalidSchema = new FormSchema(new[]
        {
            new FormFieldDefinition("a", FormFieldType.Text, "A"),
            new FormFieldDefinition("a", FormFieldType.Text, "A dup"),
        });

        var key = $"invalid-{Guid.NewGuid():N}";
        await using var db = PostgresFixture.CreateContext();
        var service = NewFormDefinitionService(db);
        var definition = await service.CreateAsync(new CreateFormDefinitionRequest(key, "Invalid", null, null));
        await service.CreateVersionAsync(definition.Id, new CreateFormVersionRequest(invalidSchema));

        await using var publishDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() => NewFormEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid()));
        Assert.Contains(ex.Errors, e => e.Code == "DUPLICATE_FIELD_KEY");

        await using var verifyDb = PostgresFixture.CreateContext();
        var stillDraft = await verifyDb.FormDefinitions.SingleAsync(f => f.Id == definition.Id);
        Assert.Equal(FormDefinitionStatus.Draft, stillDraft.Status);
    }

    [Fact]
    public async Task PublishedVersion_IsImmutable_NewEditsCreateANewVersion()
    {
        var definition = await CreateAndPublishFormAsync(PurchaseRequestSchema());

        await using var db = PostgresFixture.CreateContext();
        var v1 = await db.FormVersions.SingleAsync(v => v.FormDefinitionId == definition.Id);
        var v1SchemaJson = v1.SchemaJson;

        var service = NewFormDefinitionService(db);
        var v2 = await service.CreateVersionAsync(definition.Id, new CreateFormVersionRequest(new FormSchema(new[]
        {
            new FormFieldDefinition("itemName", FormFieldType.Text, "Item Name", Required: true),
        })));

        await using var verifyDb = PostgresFixture.CreateContext();
        var v1Reloaded = await verifyDb.FormVersions.SingleAsync(v => v.Id == v1.Id);
        Assert.Equal(v1SchemaJson, v1Reloaded.SchemaJson);
        Assert.Equal(FormVersionStatus.Published, v1Reloaded.Status);
        Assert.Equal(2, v2.VersionNumber);
        Assert.Equal(FormVersionStatus.Draft, v2.Status);
    }

    // ---- Instance / Data lifecycle ----

    [Fact]
    public async Task CreateInstance_FromDraftDefinition_IsRejected()
    {
        var key = $"draft-{Guid.NewGuid():N}";
        await using var db = PostgresFixture.CreateContext();
        var service = NewFormDefinitionService(db);
        await service.CreateAsync(new CreateFormDefinitionRequest(key, "Never Published", null, null));

        await using var engineDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewFormEngine(engineDb).CreateInstanceAsync(new CreateFormInstanceRequest(key, null), Guid.NewGuid()));
        Assert.Equal("FORM_DEFINITION_NOT_PUBLISHED", ex.Code);
    }

    [Fact]
    public async Task SaveData_ThenSubmit_RoundTripsCorrectly()
    {
        var definition = await CreateAndPublishFormAsync(PurchaseRequestSchema());
        var creator = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        await using var readDb = PostgresFixture.CreateContext();
        var queryService = new FormInstanceQueryService(readDb, new FormAuthorizationService(readDb));
        var initialData = await queryService.GetDataAsync(instance.Id, creator, Array.Empty<string>());
        Assert.NotNull(initialData);

        var payload = JsonSerializer.Deserialize<JsonElement>("""{"itemName":"Server","quantity":2,"amount":50000}""");
        await using var saveDb = PostgresFixture.CreateContext();
        var saved = await NewFormEngine(saveDb).SaveDataAsync(instance.Id, new UpdateFormDataRequest(payload, initialData!.Version), creator, Array.Empty<string>());
        Assert.Equal("Server", saved.Data.GetProperty("itemName").GetString());

        await using var submitDb = PostgresFixture.CreateContext();
        var submitted = await NewFormEngine(submitDb).SubmitAsync(instance.Id, creator, Array.Empty<string>());
        Assert.Equal(FormInstanceStatus.Submitted, submitted.Status);
    }

    [Fact]
    public async Task Submit_MissingRequiredField_IsRejected()
    {
        var definition = await CreateAndPublishFormAsync(PurchaseRequestSchema());
        var creator = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        await using var submitDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() => NewFormEngine(submitDb).SubmitAsync(instance.Id, creator, Array.Empty<string>()));
        Assert.Contains(ex.Errors, e => e.Code == "FIELD_REQUIRED");
    }

    [Fact]
    public async Task SaveData_AfterSubmit_IsRejected_FormNoLongerEditable()
    {
        var definition = await CreateAndPublishFormAsync(PurchaseRequestSchema());
        var creator = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        var payload = JsonSerializer.Deserialize<JsonElement>("""{"itemName":"x","quantity":1,"amount":1}""");
        await using var saveDb = PostgresFixture.CreateContext();
        var initialVersion = Convert.ToBase64String((await saveDb.FormData.SingleAsync(d => d.FormInstanceId == instance.Id)).RowVersion);
        await NewFormEngine(saveDb).SaveDataAsync(instance.Id, new UpdateFormDataRequest(payload, initialVersion), creator, Array.Empty<string>());

        await using var submitDb = PostgresFixture.CreateContext();
        await NewFormEngine(submitDb).SubmitAsync(instance.Id, creator, Array.Empty<string>());

        await using var editDb = PostgresFixture.CreateContext();
        var latestVersion = Convert.ToBase64String((await editDb.FormData.SingleAsync(d => d.FormInstanceId == instance.Id)).RowVersion);
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewFormEngine(editDb).SaveDataAsync(instance.Id, new UpdateFormDataRequest(payload, latestVersion), creator, Array.Empty<string>()));
        Assert.Equal("FORM_NOT_EDITABLE", ex.Code);
    }

    [Fact]
    public async Task ReadOnlyField_CannotBeChangedByClient()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("amount", FormFieldType.Number, "Amount"),
            new FormFieldDefinition("status", FormFieldType.Text, "Status", ReadOnly: true, DefaultValue: "pending"),
        });
        var definition = await CreateAndPublishFormAsync(schema);
        var creator = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        // Seed an initial "status" value directly (simulating a system-set readonly field), then
        // attempt to overwrite it through the normal save path.
        await using var seedDb = PostgresFixture.CreateContext();
        var data = await seedDb.FormData.SingleAsync(d => d.FormInstanceId == instance.Id);
        data.DataJson = """{"status":"locked-value"}""";
        await seedDb.SaveChangesAsync();

        await using var attackDb = PostgresFixture.CreateContext();
        var currentVersion = Convert.ToBase64String((await attackDb.FormData.SingleAsync(d => d.FormInstanceId == instance.Id)).RowVersion);
        var maliciousPayload = JsonSerializer.Deserialize<JsonElement>("""{"amount":100,"status":"hacked"}""");
        var result = await NewFormEngine(attackDb).SaveDataAsync(instance.Id, new UpdateFormDataRequest(maliciousPayload, currentVersion), creator, Array.Empty<string>());

        Assert.Equal("locked-value", result.Data.GetProperty("status").GetString());
        Assert.Equal(100, result.Data.GetProperty("amount").GetInt32());
    }

    // ---- Security ----

    [Fact]
    public async Task GetData_ByUnrelatedUser_IsForbidden()
    {
        var definition = await CreateAndPublishFormAsync(PurchaseRequestSchema());
        var creator = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        await using var db = PostgresFixture.CreateContext();
        var queryService = new FormInstanceQueryService(db, new FormAuthorizationService(db));
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() => queryService.GetDataAsync(instance.Id, stranger, Array.Empty<string>()));
        Assert.Equal("FORM_INSTANCE_NOT_AUTHORIZED", ex.Code);
    }

    [Fact]
    public async Task SaveData_ByUnrelatedUser_IsForbidden()
    {
        var definition = await CreateAndPublishFormAsync(PurchaseRequestSchema());
        var creator = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        var payload = JsonSerializer.Deserialize<JsonElement>("""{"itemName":"x"}""");
        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() =>
            NewFormEngine(db).SaveDataAsync(instance.Id, new UpdateFormDataRequest(payload, Convert.ToBase64String(Guid.Empty.ToByteArray())), stranger, Array.Empty<string>()));
        Assert.Equal("FORM_INSTANCE_NOT_AUTHORIZED", ex.Code);
    }

    [Fact]
    public async Task Cancel_ByNonCreatorNonAdmin_IsForbidden()
    {
        var definition = await CreateAndPublishFormAsync(PurchaseRequestSchema());
        var creator = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        await using var db = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ForbiddenAppException>(() => NewFormEngine(db).CancelAsync(instance.Id, stranger, Array.Empty<string>()));
        Assert.Equal("FORM_INSTANCE_NOT_AUTHORIZED", ex.Code);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task SaveData_ConcurrentEdits_SecondGetsConflict()
    {
        var definition = await CreateAndPublishFormAsync(PurchaseRequestSchema());
        var creator = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        await using var readDb = PostgresFixture.CreateContext();
        var queryService = new FormInstanceQueryService(readDb, new FormAuthorizationService(readDb));
        var original = await queryService.GetDataAsync(instance.Id, creator, Array.Empty<string>());

        // Both "users" read the same original version, then both try to save.
        var payloadA = JsonSerializer.Deserialize<JsonElement>("""{"itemName":"A"}""");
        var payloadB = JsonSerializer.Deserialize<JsonElement>("""{"itemName":"B"}""");

        await using var dbA = PostgresFixture.CreateContext();
        await NewFormEngine(dbA).SaveDataAsync(instance.Id, new UpdateFormDataRequest(payloadA, original!.Version), creator, Array.Empty<string>());

        await using var dbB = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ConflictAppException>(() =>
            NewFormEngine(dbB).SaveDataAsync(instance.Id, new UpdateFormDataRequest(payloadB, original.Version), creator, Array.Empty<string>()));
        Assert.Equal("FORM_CONCURRENCY_CONFLICT", ex.Code);

        await using var verifyDb = PostgresFixture.CreateContext();
        var finalData = await verifyDb.FormData.SingleAsync(d => d.FormInstanceId == instance.Id);
        Assert.Contains("\"A\"", finalData.DataJson);
    }

    // ---- Full workflow + form + approval integration ----

    [Fact]
    public async Task FullScenario_FormBoundUserTask_ThenApproval_CompletesProcess()
    {
        var approver = Guid.NewGuid();
        var formDefinition = await CreateAndPublishFormAsync(PurchaseRequestSchema(), $"purchase-{Guid.NewGuid():N}");

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("submitRequest", WorkflowNodeType.UserTask, "Submit Request",
                    Assignment: new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, ""),
                    Form: new FormReference(formDefinition.Key)),
                new WorkflowNodeDefinition("managerApproval", WorkflowNodeType.ApprovalTask, "Manager Approval", Approval: new ApprovalConfig(
                    ApprovalPolicy.AnyOne,
                    new[] { new WorkflowAssignment(WorkflowAssignmentType.User, approver.ToString()) })),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "submitRequest"),
                new WorkflowTransitionDefinition("t2", "submitRequest", "managerApproval"),
                new WorkflowTransitionDefinition("t3", "managerApproval", "end"),
            });

        var processKey = $"purchase-process-{Guid.NewGuid():N}";
        await using var setupDb = PostgresFixture.CreateContext();
        var processService = NewProcessDefinitionService(setupDb);
        var processDefinition = await processService.CreateAsync(new CreateProcessDefinitionRequest(processKey, "Purchase Process", null, null));
        await processService.CreateVersionAsync(processDefinition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        await NewWorkflowEngine(publishDb).PublishVersionAsync(processDefinition.Id, Guid.NewGuid());

        var initiator = Guid.NewGuid();
        await using var startDb = PostgresFixture.CreateContext();
        var processInstance = await NewWorkflowEngine(startDb).StartProcessAsync(new StartProcessRequest(processKey, "PR-1"), initiator);

        // The engine should have auto-created a FormInstance for the initiator's UserTask.
        await using var verifyDb1 = PostgresFixture.CreateContext();
        var task = await verifyDb1.TaskInstances.SingleAsync(t => t.ProcessInstanceId == processInstance.Id);
        var formInstance = await verifyDb1.FormInstances.SingleAsync(f => f.TaskInstanceId == task.Id);
        Assert.Equal(FormInstanceStatus.Draft, formInstance.Status);
        Assert.Equal(initiator, formInstance.CreatedByUserId);

        // Fill and submit the form — this should complete the UserTask and advance to the
        // ApprovalTask, atomically, without WorkflowEngine ever being told about forms directly.
        var payload = JsonSerializer.Deserialize<JsonElement>("""{"itemName":"Server","quantity":1,"amount":75000}""");
        await using var saveDb = PostgresFixture.CreateContext();
        var currentVersion = Convert.ToBase64String((await saveDb.FormData.SingleAsync(d => d.FormInstanceId == formInstance.Id)).RowVersion);
        await NewFormEngine(saveDb).SaveDataAsync(formInstance.Id, new UpdateFormDataRequest(payload, currentVersion), initiator, Array.Empty<string>());

        await using var submitDb = PostgresFixture.CreateContext();
        var submitted = await NewFormEngine(submitDb).SubmitAsync(formInstance.Id, initiator, Array.Empty<string>());
        Assert.Equal(FormInstanceStatus.Locked, submitted.Status);

        await using var verifyDb2 = PostgresFixture.CreateContext();
        var completedTask = await verifyDb2.TaskInstances.SingleAsync(t => t.Id == task.Id);
        Assert.Equal(TaskInstanceStatus.Completed, completedTask.Status);

        var approvalTask = await verifyDb2.TaskInstances.SingleAsync(t => t.ProcessInstanceId == processInstance.Id && t.NodeId == "managerApproval");
        Assert.Equal(TaskInstanceStatus.Pending, approvalTask.Status);

        // An approver should be able to read the submitted form (Skill.md §20).
        await using var readAsApproverDb = PostgresFixture.CreateContext();
        var formQueryService = new FormInstanceQueryService(readAsApproverDb, new FormAuthorizationService(readAsApproverDb));
        var formForApprover = await formQueryService.GetDataAsync(formInstance.Id, approver, Array.Empty<string>());
        Assert.NotNull(formForApprover);
        Assert.Equal("Server", formForApprover!.Data.GetProperty("itemName").GetString());

        // Approve -> process completes.
        await using var approveDb = PostgresFixture.CreateContext();
        var approveResult = await NewWorkflowEngine(approveDb).ApproveTaskAsync(approvalTask.Id, approver, Array.Empty<string>());
        Assert.Equal(TaskInstanceStatus.Completed, approveResult.Status);

        await using var finalDb = PostgresFixture.CreateContext();
        var finalInstance = await finalDb.ProcessInstances.SingleAsync(p => p.Id == processInstance.Id);
        Assert.Equal(ProcessInstanceStatus.Completed, finalInstance.Status);
    }

    [Fact]
    public async Task PublishWorkflow_ReferencingUnpublishedForm_IsRejected()
    {
        var key = $"unpublished-form-{Guid.NewGuid():N}";
        await using var formDb = PostgresFixture.CreateContext();
        await NewFormDefinitionService(formDb).CreateAsync(new CreateFormDefinitionRequest(key, "Never Published", null, null));

        var graph = new WorkflowDefinition(
            Nodes: new[]
            {
                new WorkflowNodeDefinition("start", WorkflowNodeType.Start, "Start"),
                new WorkflowNodeDefinition("submit", WorkflowNodeType.UserTask, "Submit", Assignment: new WorkflowAssignment(WorkflowAssignmentType.ProcessInitiator, ""), Form: new FormReference(key)),
                new WorkflowNodeDefinition("end", WorkflowNodeType.End, "End"),
            },
            Transitions: new[]
            {
                new WorkflowTransitionDefinition("t1", "start", "submit"),
                new WorkflowTransitionDefinition("t2", "submit", "end"),
            });

        var processKey = $"proc-{Guid.NewGuid():N}";
        await using var setupDb = PostgresFixture.CreateContext();
        var processService = NewProcessDefinitionService(setupDb);
        var processDefinition = await processService.CreateAsync(new CreateProcessDefinitionRequest(processKey, "Test", null, null));
        await processService.CreateVersionAsync(processDefinition.Id, new CreateProcessVersionRequest(graph));

        await using var publishDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() => NewWorkflowEngine(publishDb).PublishVersionAsync(processDefinition.Id, Guid.NewGuid()));
        Assert.Contains(ex.Errors, e => e.Code == "FORM_REFERENCE_INVALID");
    }

    // ---- Phase 5.4.2: Advanced Form Rules, against the real engine/database ----

    private static FormSchema ExpenseSchemaWithConditionalRequired() => new(
        new[]
        {
            new FormFieldDefinition("expenseType", FormFieldType.Select, "Expense Type", Required: true, Options: new[] { new FormFieldOption("OTHER", "Other"), new FormFieldOption("TRAVEL", "Travel") }),
            new FormFieldDefinition("description", FormFieldType.Text, "Description"),
        },
        new[]
        {
            new FormRule("r1", "description", FormRuleType.Required, new FormFieldCondition("expenseType", FormConditionOperator.Equals, "OTHER")),
        });

    [Fact]
    public async Task Submit_ConditionallyRequiredFieldOmitted_IsRejected_ClientCannotBypassByOmittingHiddenField()
    {
        // Phase 5.4.2 §12's exact scenario: expenseType=OTHER makes description required. A client
        // that simply doesn't submit description must still be rejected by the server.
        var definition = await CreateAndPublishFormAsync(ExpenseSchemaWithConditionalRequired());
        var creator = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        await using var readDb = PostgresFixture.CreateContext();
        var queryService = new FormInstanceQueryService(readDb, new FormAuthorizationService(readDb));
        var initialData = await queryService.GetDataAsync(instance.Id, creator, Array.Empty<string>());

        var payload = JsonSerializer.Deserialize<JsonElement>("""{"expenseType":"OTHER"}""");
        await using var saveDb = PostgresFixture.CreateContext();
        await NewFormEngine(saveDb).SaveDataAsync(instance.Id, new UpdateFormDataRequest(payload, initialData!.Version), creator, Array.Empty<string>());

        await using var submitDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() => NewFormEngine(submitDb).SubmitAsync(instance.Id, creator, Array.Empty<string>()));
        Assert.Contains(ex.Errors, e => e.Code == "FIELD_REQUIRED");
    }

    [Fact]
    public async Task Submit_ConditionallyRequiredFieldNotTriggered_SubmissionSucceeds()
    {
        var definition = await CreateAndPublishFormAsync(ExpenseSchemaWithConditionalRequired());
        var creator = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        await using var readDb = PostgresFixture.CreateContext();
        var queryService = new FormInstanceQueryService(readDb, new FormAuthorizationService(readDb));
        var initialData = await queryService.GetDataAsync(instance.Id, creator, Array.Empty<string>());

        var payload = JsonSerializer.Deserialize<JsonElement>("""{"expenseType":"TRAVEL"}""");
        await using var saveDb = PostgresFixture.CreateContext();
        await NewFormEngine(saveDb).SaveDataAsync(instance.Id, new UpdateFormDataRequest(payload, initialData!.Version), creator, Array.Empty<string>());

        await using var submitDb = PostgresFixture.CreateContext();
        var submitted = await NewFormEngine(submitDb).SubmitAsync(instance.Id, creator, Array.Empty<string>());
        Assert.Equal(FormInstanceStatus.Submitted, submitted.Status);
    }

    [Fact]
    public async Task SaveData_CalculatedField_ServerRecomputes_ClientValueNotTrusted()
    {
        var schema = new FormSchema(
            new[]
            {
                new FormFieldDefinition("quantity", FormFieldType.Number, "Quantity", Required: true),
                new FormFieldDefinition("unitPrice", FormFieldType.Number, "Unit Price", Required: true),
                new FormFieldDefinition("total", FormFieldType.Number, "Total"),
            },
            new[] { new FormRule("r1", "total", FormRuleType.Calculated, Formula: "quantity * unitPrice") });

        var definition = await CreateAndPublishFormAsync(schema);
        var creator = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        await using var readDb = PostgresFixture.CreateContext();
        var queryService = new FormInstanceQueryService(readDb, new FormAuthorizationService(readDb));
        var initialData = await queryService.GetDataAsync(instance.Id, creator, Array.Empty<string>());

        // Client submits a spoofed total (999999) alongside the real inputs.
        var payload = JsonSerializer.Deserialize<JsonElement>("""{"quantity":3,"unitPrice":100,"total":999999}""");
        await using var saveDb = PostgresFixture.CreateContext();
        var saved = await NewFormEngine(saveDb).SaveDataAsync(instance.Id, new UpdateFormDataRequest(payload, initialData!.Version), creator, Array.Empty<string>());

        Assert.Equal(300m, saved.Data.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task CreateInstance_AppliesDeclaredDefaultValues_UserSubmittedValueIsNeverOverwritten()
    {
        var schema = new FormSchema(new[]
        {
            new FormFieldDefinition("country", FormFieldType.Text, "Country", DefaultValue: "Taiwan"),
            new FormFieldDefinition("quantity", FormFieldType.Number, "Quantity", DefaultValue: "1"),
        });
        var definition = await CreateAndPublishFormAsync(schema);
        var creator = Guid.NewGuid();

        await using var createDb = PostgresFixture.CreateContext();
        var instance = await NewFormEngine(createDb).CreateInstanceAsync(new CreateFormInstanceRequest(definition.Key, null), creator);

        await using var readDb = PostgresFixture.CreateContext();
        var queryService = new FormInstanceQueryService(readDb, new FormAuthorizationService(readDb));
        var initialData = await queryService.GetDataAsync(instance.Id, creator, Array.Empty<string>());
        Assert.Equal("Taiwan", initialData!.Data.GetProperty("country").GetString());
        Assert.Equal(1m, initialData.Data.GetProperty("quantity").GetDecimal());

        // The user overrides quantity — the default must never overwrite it on a later save.
        var payload = JsonSerializer.Deserialize<JsonElement>("""{"quantity":5}""");
        await using var saveDb = PostgresFixture.CreateContext();
        var saved = await NewFormEngine(saveDb).SaveDataAsync(instance.Id, new UpdateFormDataRequest(payload, initialData.Version), creator, Array.Empty<string>());
        Assert.Equal(5m, saved.Data.GetProperty("quantity").GetDecimal());
        Assert.Equal("Taiwan", saved.Data.GetProperty("country").GetString());
    }

    [Fact]
    public async Task PublishVersion_SchemaWithCircularRuleDependency_RejectedAndNotPublished()
    {
        var schema = new FormSchema(
            new[]
            {
                new FormFieldDefinition("a", FormFieldType.Text, "A"),
                new FormFieldDefinition("b", FormFieldType.Text, "B"),
            },
            new[]
            {
                new FormRule("r1", "a", FormRuleType.Visibility, new FormFieldCondition("b", FormConditionOperator.Equals, "x")),
                new FormRule("r2", "b", FormRuleType.Visibility, new FormFieldCondition("a", FormConditionOperator.Equals, "x")),
            });

        var key = $"circular-{Guid.NewGuid():N}";
        await using var db = PostgresFixture.CreateContext();
        var service = NewFormDefinitionService(db);
        var definition = await service.CreateAsync(new CreateFormDefinitionRequest(key, "Circular", null, null));
        await service.CreateVersionAsync(definition.Id, new CreateFormVersionRequest(schema));

        await using var publishDb = PostgresFixture.CreateContext();
        var ex = await Assert.ThrowsAsync<ValidationAppException>(() => NewFormEngine(publishDb).PublishVersionAsync(definition.Id, Guid.NewGuid()));
        Assert.Contains(ex.Errors, e => e.Code == "CIRCULAR_RULE_DEPENDENCY");
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
