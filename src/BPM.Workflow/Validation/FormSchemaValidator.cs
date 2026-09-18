using BPM.Domain.Forms;

namespace BPM.Workflow.Validation;

// Publish-time validation for FormVersion.SchemaJson (Skill.md Phase 4 §10). Pure and DB-free,
// same design as WorkflowDefinitionValidator: it operates only on the already-deserialized
// FormSchema, so it's trivially unit-testable and fails closed — any violation blocks publish.
public static class FormSchemaValidator
{
    // Field types the engine can actually render/validate. FormFieldType has more members
    // (reserved for later phases); using one of those today is rejected here rather than
    // accepted and silently ignored at runtime.
    private static readonly HashSet<FormFieldType> SupportedFieldTypes = new()
    {
        FormFieldType.Text,
        FormFieldType.Textarea,
        FormFieldType.Number,
        FormFieldType.Currency,
        FormFieldType.Date,
        FormFieldType.DateTime,
        FormFieldType.Select,
        FormFieldType.Radio,
        FormFieldType.Checkbox,
        FormFieldType.User,
        FormFieldType.Department,
        FormFieldType.File,
    };

    private static readonly HashSet<FormFieldType> OptionBasedTypes = new()
    {
        FormFieldType.Select,
        FormFieldType.Radio,
    };

    private static readonly HashSet<FormFieldType> NumericTypes = new()
    {
        FormFieldType.Number,
        FormFieldType.Currency,
    };

    public static WorkflowValidationResult Validate(FormSchema? schema)
    {
        var result = new WorkflowValidationResult();

        if (schema is null || schema.Fields.Count == 0)
        {
            result.AddError("EMPTY_SCHEMA", "Form schema must contain at least one field.");
            return result;
        }

        var fieldKeys = new HashSet<string>();
        foreach (var field in schema.Fields)
        {
            ValidateKey(field, fieldKeys, result);
            ValidateType(field, result);
            ValidateOptions(field, result);
            ValidateValidationRules(field, result);
        }

        // Visibility references (and, below, Rules) assume every field's key is sound — skip if
        // the key set itself is unreliable (duplicates/invalid already reported above).
        if (result.IsValid)
        {
            ValidateVisibilityReferences(schema, result);
        }

        // Phase 5.4.2: FormSchema.Rules, additive to the field-level checks above. A separate
        // internal validator (not inlined here) since it's a materially larger, self-contained
        // concern (condition trees, formulas, dependency cycles) — same reasoning that already
        // split FormDataValidator out from this class for a different question ("is this schema
        // well-formed" vs "does this data satisfy it").
        if (result.IsValid)
        {
            FormRuleValidator.Validate(schema, result);
        }

        return result;
    }

    private static void ValidateKey(FormFieldDefinition field, HashSet<string> seen, WorkflowValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(field.Key))
        {
            result.AddError("INVALID_FIELD_KEY", "Every field must have a non-empty key.");
            return;
        }

        // Skill.md §9: keys must be stable identifiers, not display labels — enforce a safe,
        // predictable shape (letters, digits, underscore; must not start with a digit) rather
        // than accepting arbitrary text like "Item Name" or "採購金額!" as a JSON/storage key.
        if (!System.Text.RegularExpressions.Regex.IsMatch(field.Key, "^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            result.AddError("INVALID_FIELD_KEY", $"Field key '{field.Key}' must start with a letter or underscore and contain only letters, digits, and underscores.");
        }

        if (!seen.Add(field.Key))
        {
            result.AddError("DUPLICATE_FIELD_KEY", $"Field key '{field.Key}' is used more than once.");
        }
    }

    private static void ValidateType(FormFieldDefinition field, WorkflowValidationResult result)
    {
        if (!SupportedFieldTypes.Contains(field.Type))
        {
            result.AddError("UNSUPPORTED_FIELD_TYPE", $"Field '{field.Key}' has type '{field.Type}', which is not yet supported.");
        }
    }

    private static void ValidateOptions(FormFieldDefinition field, WorkflowValidationResult result)
    {
        if (!OptionBasedTypes.Contains(field.Type))
        {
            return;
        }

        if (field.Options is null || field.Options.Count == 0)
        {
            result.AddError("MISSING_FIELD_OPTIONS", $"Field '{field.Key}' of type '{field.Type}' must specify at least one option.");
            return;
        }

        var seenValues = new HashSet<string>();
        foreach (var option in field.Options)
        {
            if (string.IsNullOrWhiteSpace(option.Value))
            {
                result.AddError("INVALID_FIELD_OPTION", $"Field '{field.Key}' has an option with an empty value.");
            }
            else if (!seenValues.Add(option.Value))
            {
                result.AddError("DUPLICATE_FIELD_OPTION", $"Field '{field.Key}' has duplicate option value '{option.Value}'.");
            }
        }
    }

    private static void ValidateValidationRules(FormFieldDefinition field, WorkflowValidationResult result)
    {
        var validation = field.Validation;
        if (validation is null)
        {
            return;
        }

        if (validation.MinLength is not null && validation.MinLength < 0)
        {
            result.AddError("INVALID_VALIDATION_RULE", $"Field '{field.Key}' has a negative minLength.");
        }
        if (validation.MaxLength is not null && validation.MinLength is not null && validation.MaxLength < validation.MinLength)
        {
            result.AddError("INVALID_VALIDATION_RULE", $"Field '{field.Key}' has maxLength less than minLength.");
        }
        if (validation.MinValue is not null && validation.MaxValue is not null && validation.MaxValue < validation.MinValue)
        {
            result.AddError("INVALID_VALIDATION_RULE", $"Field '{field.Key}' has maxValue less than minValue.");
        }
        if ((validation.MinLength is not null || validation.MaxLength is not null || validation.Pattern is not null) && !IsTextLike(field.Type))
        {
            result.AddError("INVALID_VALIDATION_RULE", $"Field '{field.Key}' of type '{field.Type}' does not support length/pattern validation.");
        }
        if ((validation.MinValue is not null || validation.MaxValue is not null) && !NumericTypes.Contains(field.Type))
        {
            result.AddError("INVALID_VALIDATION_RULE", $"Field '{field.Key}' of type '{field.Type}' does not support minValue/maxValue validation.");
        }
        if (validation.Pattern is not null)
        {
            try
            {
                _ = new System.Text.RegularExpressions.Regex(validation.Pattern);
            }
            catch (ArgumentException)
            {
                result.AddError("INVALID_VALIDATION_RULE", $"Field '{field.Key}' has an invalid regular expression pattern.");
            }
        }
    }

    private static bool IsTextLike(FormFieldType type) =>
        type is FormFieldType.Text or FormFieldType.Textarea;

    private static void ValidateVisibilityReferences(FormSchema schema, WorkflowValidationResult result)
    {
        var keys = schema.Fields.Select(f => f.Key).ToHashSet();
        foreach (var field in schema.Fields.Where(f => f.Visibility is not null))
        {
            var referenced = field.Visibility!.When.Field;
            if (referenced == field.Key)
            {
                result.AddError("INVALID_VISIBILITY_RULE", $"Field '{field.Key}' cannot depend on its own value for visibility.");
            }
            else if (!keys.Contains(referenced))
            {
                result.AddError("INVALID_VISIBILITY_RULE", $"Field '{field.Key}' has a visibility rule referencing unknown field '{referenced}'.");
            }
        }
    }
}
