using System.Text.Json;
using BPM.Application.Common;
using BPM.Application.Forms;
using BPM.Application.Workflow;
using BPM.Domain.Entities;
using BPM.Domain.Forms;
using BPM.Domain.Workflow;
using BPM.Infrastructure.Persistence;
using BPM.Workflow.Validation;
using Microsoft.EntityFrameworkCore;

namespace BPM.Workflow.Engine;

// Form Engine (Skill.md Phase 4) — the Form equivalent of WorkflowEngine/ApprovalEngine. Publishes
// FormVersions (validated by FormSchemaValidator) and drives a FormInstance's lifecycle. When a
// Submit completes the UserTask this instance is attached to, it calls into WorkflowTransitions —
// the same shared "move to next node" code ApprovalEngine uses — rather than reimplementing it,
// so a form-driven task completion is exactly as atomic and auditable as any other (Skill.md §19:
// "do not hard-code form behavior into WorkflowEngine"; the integration point is one shared helper
// both engines call, not WorkflowEngine reaching into forms or vice versa).
public class FormEngine : IFormEngine
{
    private readonly BpmDbContext _db;
    private readonly IFormAuthorizationService _authorization;

    public FormEngine(BpmDbContext db, IFormAuthorizationService authorization)
    {
        _db = db;
        _authorization = authorization;
    }

    public WorkflowValidationResultDto ValidateSchema(FormSchema schema)
    {
        var result = FormSchemaValidator.Validate(schema);
        return new WorkflowValidationResultDto(
            result.IsValid,
            result.Errors.Select(e => new WorkflowValidationErrorDto(e.Code, e.Message)).ToList());
    }

    public async Task<FormVersionDto> PublishVersionAsync(Guid formDefinitionId, Guid publishedBy, CancellationToken cancellationToken = default)
    {
        var definition = await _db.FormDefinitions
            .SingleOrDefaultAsync(f => f.Id == formDefinitionId, cancellationToken)
            ?? throw new NotFoundAppException("FORM_DEFINITION_NOT_FOUND", $"Form definition '{formDefinitionId}' was not found.");

        var draft = await _db.FormVersions
            .Where(v => v.FormDefinitionId == formDefinitionId && v.Status == FormVersionStatus.Draft)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundAppException("DRAFT_VERSION_NOT_FOUND", "This form definition has no draft version to publish.");

        var parsed = FormJson.TryDeserialize(draft.SchemaJson);
        var validation = FormSchemaValidator.Validate(parsed);
        if (!validation.IsValid)
        {
            throw new ValidationAppException(
                "FORM_SCHEMA_INVALID",
                "Form schema failed validation.",
                validation.Errors.Select(e => (e.Code, e.Message)).ToList());
        }

        draft.Status = FormVersionStatus.Published;
        draft.PublishedBy = publishedBy;
        draft.PublishedAt = DateTime.UtcNow;

        definition.Status = FormDefinitionStatus.Published;
        definition.CurrentVersionId = draft.Id;

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(publishedBy, AuditActions.FormVersionPublished, nameof(FormVersion), draft.Id.ToString(), new { draft.FormDefinitionId, draft.VersionNumber }));

        await _db.SaveChangesAsync(cancellationToken);

        return new FormVersionDto(draft.Id, draft.FormDefinitionId, draft.VersionNumber, draft.Status, parsed!, draft.CreatedAt, draft.CreatedBy, draft.PublishedAt, draft.PublishedBy, Convert.ToBase64String(draft.RowVersion));
    }

    public async Task<FormInstanceDto> CreateInstanceAsync(CreateFormInstanceRequest request, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var definition = await _db.FormDefinitions
            .SingleOrDefaultAsync(f => f.Key == request.FormDefinitionKey, cancellationToken)
            ?? throw new NotFoundAppException("FORM_DEFINITION_NOT_FOUND", $"Form definition '{request.FormDefinitionKey}' was not found.");

        if (definition.Status != FormDefinitionStatus.Published || definition.CurrentVersionId is null)
        {
            throw new ConflictAppException("FORM_DEFINITION_NOT_PUBLISHED", $"Form definition '{request.FormDefinitionKey}' has no published version.");
        }

        var instance = await CreateInstanceInternalAsync(definition.Id, definition.CurrentVersionId.Value, request.ProcessInstanceId, taskInstanceId: null, currentUserId, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(instance);
    }

    // Called by WorkflowTransitions when advancing into a UserTask node that references a form
    // (Skill.md §19) — not part of IFormEngine's public surface; the workflow engine creates
    // these, a client only ever creates standalone ones via CreateInstanceAsync above.
    //
    // Phase 10 — pinnedFormVersionId is the FormVersionId WorkflowEngine.PublishVersionAsync
    // snapshotted into this node's FormReference at publish time (see its own doc comment). When
    // present, it is authoritative — never re-validated against the form's *current* published
    // version, since the whole point is deterministic behavior regardless of what's been
    // published since. It's only ever missing for a ProcessVersion published before this pinning
    // existed, in which case this falls back to the original live-lookup-at-creation-time
    // behavior (FormDefinition.CurrentVersionId) — that older version's actual historical
    // semantics are deliberately left unchanged, not retroactively pinned.
    public static async Task<FormInstance> CreateInstanceForTaskAsync(BpmDbContext db, Guid processInstanceId, Guid taskInstanceId, string formDefinitionKey, Guid? pinnedFormVersionId, Guid actingUserId, CancellationToken cancellationToken)
    {
        var definition = await db.FormDefinitions.SingleOrDefaultAsync(f => f.Key == formDefinitionKey, cancellationToken)
            ?? throw new ConflictAppException("FORM_DEFINITION_NOT_FOUND", $"Form definition '{formDefinitionKey}' referenced by the workflow was not found.");

        if (pinnedFormVersionId is { } formVersionId)
        {
            return await CreateInstanceInternalStaticAsync(db, definition.Id, formVersionId, processInstanceId, taskInstanceId, actingUserId, cancellationToken);
        }

        if (definition.Status != FormDefinitionStatus.Published || definition.CurrentVersionId is null)
        {
            throw new ConflictAppException("FORM_DEFINITION_NOT_PUBLISHED", $"Form definition '{formDefinitionKey}' referenced by the workflow has no published version.");
        }

        return await CreateInstanceInternalStaticAsync(db, definition.Id, definition.CurrentVersionId.Value, processInstanceId, taskInstanceId, actingUserId, cancellationToken);
    }

    private Task<FormInstance> CreateInstanceInternalAsync(Guid formDefinitionId, Guid formVersionId, Guid? processInstanceId, Guid? taskInstanceId, Guid actingUserId, CancellationToken cancellationToken) =>
        CreateInstanceInternalStaticAsync(_db, formDefinitionId, formVersionId, processInstanceId, taskInstanceId, actingUserId, cancellationToken);

    private static async Task<FormInstance> CreateInstanceInternalStaticAsync(BpmDbContext db, Guid formDefinitionId, Guid formVersionId, Guid? processInstanceId, Guid? taskInstanceId, Guid actingUserId, CancellationToken cancellationToken)
    {
        var instance = new FormInstance
        {
            FormDefinitionId = formDefinitionId,
            FormVersionId = formVersionId,
            ProcessInstanceId = processInstanceId,
            TaskInstanceId = taskInstanceId,
            CreatedByUserId = actingUserId,
            Status = FormInstanceStatus.Draft,
        };
        db.FormInstances.Add(instance);

        var version = await db.FormVersions.AsNoTracking().SingleAsync(v => v.Id == formVersionId, cancellationToken);
        var schema = FormJson.TryDeserialize(version.SchemaJson)
            ?? throw new ConflictAppException("FORM_VERSION_CORRUPT", "The form version's schema could not be parsed.");

        db.FormData.Add(new BPM.Domain.Entities.FormData
        {
            FormInstanceId = instance.Id,
            DataJson = BuildInitialDataJson(schema),
        });

        db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(actingUserId, AuditActions.FormInstanceCreated, nameof(FormInstance), instance.Id.ToString(), new { formDefinitionId, formVersionId, processInstanceId, taskInstanceId }));

        await Task.CompletedTask;
        return instance;
    }

    public async Task<FormDataDto> SaveDataAsync(Guid formInstanceId, UpdateFormDataRequest request, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var (instance, data, schema) = await LoadEditableInstanceAsync(formInstanceId, currentUserId, currentUserRoles, cancellationToken);

        var validation = FormDataValidator.Validate(schema, request.Data, enforceRequired: false);
        if (!validation.IsValid)
        {
            throw new ValidationAppException("FORM_DATA_INVALID", "Form data failed validation.", validation.Errors.Select(e => (e.Code, e.Message)).ToList());
        }

        var mergedJson = MergeEditableFields(schema, data.DataJson, request.Data);

        // Phase 5.4.2: recompute every Calculated field server-side before persisting — a client
        // can submit whatever it wants for a calculated field's value, but it is never trusted
        // (§14); what actually gets stored is always what the rule engine itself computed. A
        // formula that can't be safely evaluated right now (division by zero) fails the save
        // closed rather than persisting a wrong or stale number.
        var evaluated = FormRuleEngine.Evaluate(schema, JsonSerializer.Deserialize<JsonElement>(mergedJson));
        if (evaluated.CalculationErrors.Count > 0)
        {
            throw new ValidationAppException(
                "FORM_DATA_INVALID",
                "Form data failed validation.",
                evaluated.CalculationErrors.Select(e => ("FIELD_FORMULA_EVALUATION_ERROR", $"Field '{e.Key}' could not be calculated: {e.Value}.")).ToList());
        }

        data.DataJson = JsonSerializer.Serialize(evaluated.Data);

        var expectedVersion = Convert.FromBase64String(request.ExpectedVersion);
        _db.Entry(data).Property(d => d.RowVersion).OriginalValue = expectedVersion;

        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.FormDataUpdated, nameof(BPM.Domain.Entities.FormData), data.Id.ToString(), new { formInstanceId }));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Skill.md §26: someone else saved since the caller last read this form.
            throw new ConflictAppException("FORM_CONCURRENCY_CONFLICT", "This form was modified by another request. Reload and retry.");
        }

        return new FormDataDto(formInstanceId, JsonSerializer.Deserialize<JsonElement>(data.DataJson), Convert.ToBase64String(data.RowVersion));
    }

    public async Task<FormInstanceDto> SubmitAsync(Guid formInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var (instance, data, schema) = await LoadEditableInstanceAsync(formInstanceId, currentUserId, currentUserRoles, cancellationToken);

        // Phase 5.4.2 §12: authoritative re-evaluation at submission time, not just Save. Recompute
        // Calculated fields one last time (never trust whatever happens to be stored) and derive
        // the *effective* required set (static + conditionally-required rules) — a hidden field
        // that a client omitted is still validated exactly like a visible one; visibility never
        // exempts a field from a required check (§12's own example: submitting without a
        // conditionally-required field must be rejected regardless of whether it was shown).
        var evaluated = FormRuleEngine.Evaluate(schema, JsonSerializer.Deserialize<JsonElement>(data.DataJson));
        if (evaluated.CalculationErrors.Count > 0)
        {
            throw new ValidationAppException(
                "FORM_DATA_INVALID",
                "Form data failed validation.",
                evaluated.CalculationErrors.Select(e => ("FIELD_FORMULA_EVALUATION_ERROR", $"Field '{e.Key}' could not be calculated: {e.Value}.")).ToList());
        }
        data.DataJson = JsonSerializer.Serialize(evaluated.Data);

        var requiredKeys = evaluated.Fields.Where(f => f.Value.Required).Select(f => f.Key).ToHashSet();
        var validation = FormDataValidator.Validate(schema, evaluated.Data, enforceRequired: true, requiredKeys);
        if (!validation.IsValid)
        {
            throw new ValidationAppException("FORM_DATA_INVALID", "Form data failed validation.", validation.Errors.Select(e => (e.Code, e.Message)).ToList());
        }

        instance.Status = FormInstanceStatus.Submitted;
        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.FormSubmitted, nameof(FormInstance), instance.Id.ToString()));

        if (instance.TaskInstanceId is Guid taskInstanceId)
        {
            await CompleteLinkedTaskAsync(instance, taskInstanceId, currentUserId, cancellationToken);
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("FORM_CONCURRENCY_CONFLICT", "This form was modified by another request. Reload and retry.");
        }

        return ToDto(instance);
    }

    private async Task CompleteLinkedTaskAsync(FormInstance instance, Guid taskInstanceId, Guid currentUserId, CancellationToken cancellationToken)
    {
        var task = await _db.TaskInstances.Include(t => t.ProcessInstance).SingleAsync(t => t.Id == taskInstanceId, cancellationToken);

        var isApprovalTask = await _db.ApprovalInstances.AnyAsync(a => a.TaskInstanceId == taskInstanceId, cancellationToken);
        if (isApprovalTask)
        {
            // A form can only be auto-created for a plain UserTask node (see
            // WorkflowTransitions.CreateTaskForNodeAsync) — reaching this means the data is
            // inconsistent; fail closed rather than silently completing an approval task.
            throw new ConflictAppException("TASK_IS_APPROVAL_TASK", $"Task '{taskInstanceId}' is an approval task and cannot be completed by submitting a form.");
        }

        // Idempotency (Skill.md §27): if the linked task is already resolved (e.g. this submit is
        // a retry, or the task was completed through some other path), just leave the
        // FormInstance at Submitted rather than throwing — the caller already got a success
        // response for the original request; a retry should look like success too, not a 409.
        if (task.Status != TaskInstanceStatus.Pending && task.Status != TaskInstanceStatus.InProgress)
        {
            return;
        }
        if (task.ProcessInstance!.Status != ProcessInstanceStatus.Running)
        {
            return;
        }

        task.Status = TaskInstanceStatus.Completed;
        task.CompletedAt = DateTime.UtcNow;
        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.TaskCompleted, nameof(TaskInstance), task.Id.ToString(), new { task.NodeId, task.ProcessInstanceId }));

        var version = await _db.ProcessVersions.SingleAsync(v => v.Id == task.ProcessInstance.ProcessVersionId, cancellationToken);
        var graph = WorkflowJson.TryDeserialize(version.DefinitionJson)
            ?? throw new ConflictAppException("PROCESS_VERSION_CORRUPT", "The process version's definition could not be parsed.");
        var currentNode = graph.Nodes.Single(n => n.Id == task.NodeId);

        await WorkflowTransitions.AdvanceFromAsync(_db, graph, currentNode, task.ProcessInstance, currentUserId, cancellationToken);

        // Skill.md §18: "a submitted/locked form must not be freely editable" — once the task it
        // fed is done, the form is done too.
        instance.Status = FormInstanceStatus.Locked;
        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.FormLocked, nameof(FormInstance), instance.Id.ToString()));
    }

    public async Task<FormInstanceDto> CancelAsync(Guid formInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken = default)
    {
        var instance = await _db.FormInstances.SingleOrDefaultAsync(f => f.Id == formInstanceId, cancellationToken)
            ?? throw new NotFoundAppException("FORM_INSTANCE_NOT_FOUND", $"Form instance '{formInstanceId}' was not found.");

        // Cancel is creator/admin-only — stricter than edit, since a task assignee who merely has
        // edit rights on someone else's form shouldn't be able to withdraw it.
        if (instance.CreatedByUserId != currentUserId && !currentUserRoles.Contains("Administrator"))
        {
            throw new ForbiddenAppException("FORM_INSTANCE_NOT_AUTHORIZED", "Only the creator or an administrator may cancel this form.");
        }

        if (instance.Status is FormInstanceStatus.Locked or FormInstanceStatus.Cancelled)
        {
            throw new ConflictAppException("FORM_ALREADY_FINALIZED", $"Form instance '{formInstanceId}' is already {instance.Status} and cannot be cancelled.");
        }

        instance.Status = FormInstanceStatus.Cancelled;
        _db.AuditLogs.Add(WorkflowTransitions.NewAuditLog(currentUserId, AuditActions.FormCancelled, nameof(FormInstance), instance.Id.ToString()));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictAppException("FORM_CONCURRENCY_CONFLICT", "This form was modified by another request. Reload and retry.");
        }

        return ToDto(instance);
    }

    private async Task<(FormInstance Instance, BPM.Domain.Entities.FormData Data, FormSchema Schema)> LoadEditableInstanceAsync(Guid formInstanceId, Guid currentUserId, IReadOnlyCollection<string> currentUserRoles, CancellationToken cancellationToken)
    {
        var instance = await _db.FormInstances.SingleOrDefaultAsync(f => f.Id == formInstanceId, cancellationToken)
            ?? throw new NotFoundAppException("FORM_INSTANCE_NOT_FOUND", $"Form instance '{formInstanceId}' was not found.");

        if (!await _authorization.CanEditAsync(instance, currentUserId, currentUserRoles, cancellationToken))
        {
            throw new ForbiddenAppException("FORM_INSTANCE_NOT_AUTHORIZED", "You are not authorized to edit this form.");
        }

        if (instance.Status != FormInstanceStatus.Draft)
        {
            throw new ConflictAppException("FORM_NOT_EDITABLE", $"Form instance '{formInstanceId}' is {instance.Status} and can no longer be edited.");
        }

        var data = await _db.FormData.SingleAsync(d => d.FormInstanceId == formInstanceId, cancellationToken);

        var version = await _db.FormVersions.AsNoTracking().SingleAsync(v => v.Id == instance.FormVersionId, cancellationToken);
        var schema = FormJson.TryDeserialize(version.SchemaJson)
            ?? throw new ConflictAppException("FORM_VERSION_CORRUPT", "The form version's schema could not be parsed.");

        return (instance, data, schema);
    }

    // Phase 5.4.2 §15/D: applies each field's existing (Phase 4) FormFieldDefinition.DefaultValue
    // at instance creation — that value was accepted and stored since Phase 4 but never actually
    // used anywhere at runtime until now. Reusing it (rather than inventing a parallel
    // rule-based default mechanism) satisfies "declarative default values" with no new schema
    // surface: a brand-new instance's DataJson starts empty, so writing defaults in at creation
    // trivially guarantees they never overwrite an explicit user value — there isn't one yet.
    private static string BuildInitialDataJson(FormSchema schema)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var field in schema.Fields)
            {
                if (field.DefaultValue is null) continue;
                writer.WritePropertyName(field.Key);
                WriteTypedDefault(writer, field);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteTypedDefault(Utf8JsonWriter writer, FormFieldDefinition field)
    {
        switch (field.Type)
        {
            case FormFieldType.Number:
            case FormFieldType.Currency:
                if (decimal.TryParse(field.DefaultValue, System.Globalization.CultureInfo.InvariantCulture, out var num))
                {
                    writer.WriteNumberValue(num);
                }
                else
                {
                    writer.WriteStringValue(field.DefaultValue);
                }
                break;

            case FormFieldType.Checkbox:
                if (bool.TryParse(field.DefaultValue, out var b))
                {
                    writer.WriteBooleanValue(b);
                }
                else
                {
                    writer.WriteStringValue(field.DefaultValue);
                }
                break;

            default:
                writer.WriteStringValue(field.DefaultValue);
                break;
        }
    }

    // Skill.md §17: "the client must not be able to bypass [readonly] by modifying JSON." A save
    // is a merge, not a wholesale replace: readonly fields keep whatever value they already had
    // in storage regardless of what the incoming payload says (even if the client omits them, or
    // sends a different value) — only non-readonly fields are updated from the incoming payload.
    private static string MergeEditableFields(FormSchema schema, string existingJson, JsonElement incoming)
    {
        var readOnlyKeys = schema.Fields.Where(f => f.ReadOnly).Select(f => f.Key).ToHashSet();

        using var existingDoc = JsonDocument.Parse(existingJson);
        var merged = new Dictionary<string, JsonElement>();

        foreach (var property in existingDoc.RootElement.EnumerateObject())
        {
            merged[property.Name] = property.Value.Clone();
        }

        foreach (var property in incoming.EnumerateObject())
        {
            if (!readOnlyKeys.Contains(property.Name))
            {
                merged[property.Name] = property.Value.Clone();
            }
        }

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in merged)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static FormInstanceDto ToDto(FormInstance instance) =>
        new(instance.Id, instance.FormDefinitionId, instance.FormVersionId, instance.ProcessInstanceId, instance.TaskInstanceId, instance.CreatedByUserId, instance.Status);
}
