using System.Text.Json;
using BPM.Domain.Forms;
using BPM.Workflow.Validation;

namespace BPM.Workflow.Engine;

// Validates submitted field *values* against a FormSchema (Skill.md Phase 4 §14/§15) — distinct
// from FormSchemaValidator, which validates the schema definition itself at publish time. Pure
// and DB-free for the same reason WorkflowDefinitionValidator/FormSchemaValidator are: no
// executable expressions, ever — just declarative type/range/length checks.
// Public (not internal), matching WorkflowDefinitionValidator/FormSchemaValidator, so it's
// directly unit-testable without needing InternalsVisibleTo.
public static class FormDataValidator
{
    // enforceRequired is false for a Draft save (partial data is fine while still editing) and
    // true for Submit (Skill.md §14: "the backend must perform validation" before accepting a
    // submission). requiredOverride (Phase 5.4.2), when provided, is the *effective* required set
    // computed by FormRuleEngine (static field.Required OR-ed with any matching conditionally-
    // required rule) and fully replaces the plain field.Required check — a client cannot dodge a
    // conditionally-required field by pointing at its static schema flag. Left null, behavior is
    // identical to every pre-5.4.2 call site (existing tests pass this positionally and keep
    // working unchanged).
    public static WorkflowValidationResult Validate(FormSchema schema, JsonElement data, bool enforceRequired, IReadOnlySet<string>? requiredOverride = null)
    {
        var result = new WorkflowValidationResult();

        if (data.ValueKind != JsonValueKind.Object)
        {
            result.AddError("FORM_DATA_NOT_AN_OBJECT", "Form data must be a JSON object.");
            return result;
        }

        foreach (var field in schema.Fields)
        {
            var hasValue = data.TryGetProperty(field.Key, out var value) && value.ValueKind != JsonValueKind.Null;
            var isRequired = requiredOverride?.Contains(field.Key) ?? field.Required;

            if (!hasValue)
            {
                if (enforceRequired && isRequired)
                {
                    result.AddError("FIELD_REQUIRED", $"Field '{field.Key}' is required.");
                }
                continue;
            }

            if (field.ReadOnly)
            {
                // Skill.md §17: the client must not be able to bypass readonly by editing JSON.
                // We don't know the *previous* value here (that's a diff FormEngine can do before
                // calling this), so this only catches the simplest case; FormEngine additionally
                // strips readonly fields from the incoming payload before persisting.
                continue;
            }

            ValidateFieldValue(field, value, result);
        }

        return result;
    }

    private static void ValidateFieldValue(FormFieldDefinition field, JsonElement value, WorkflowValidationResult result)
    {
        switch (field.Type)
        {
            case FormFieldType.Number:
            case FormFieldType.Currency:
                ValidateNumber(field, value, result);
                break;

            case FormFieldType.Text:
            case FormFieldType.Textarea:
                ValidateText(field, value, result);
                break;

            case FormFieldType.Select:
            case FormFieldType.Radio:
                ValidateOption(field, value, result);
                break;

            case FormFieldType.Checkbox:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    result.AddError("FIELD_TYPE_MISMATCH", $"Field '{field.Key}' must be a boolean.");
                }
                break;

            case FormFieldType.Date:
            case FormFieldType.DateTime:
                if (value.ValueKind != JsonValueKind.String || !DateTime.TryParse(value.GetString(), out _))
                {
                    result.AddError("FIELD_TYPE_MISMATCH", $"Field '{field.Key}' must be a valid date.");
                }
                break;

            case FormFieldType.User:
            case FormFieldType.Department:
                if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out _))
                {
                    result.AddError("FIELD_TYPE_MISMATCH", $"Field '{field.Key}' must be a valid id.");
                }
                break;

            case FormFieldType.File:
                if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Array))
                {
                    result.AddError("FIELD_TYPE_MISMATCH", $"Field '{field.Key}' must be an attachment id or array of attachment ids.");
                }
                break;
        }
    }

    private static void ValidateNumber(FormFieldDefinition field, JsonElement value, WorkflowValidationResult result)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
        {
            result.AddError("FIELD_TYPE_MISMATCH", $"Field '{field.Key}' must be a number.");
            return;
        }

        var validation = field.Validation;
        if (validation?.MinValue is decimal min && number < min)
        {
            result.AddError("FIELD_OUT_OF_RANGE", $"Field '{field.Key}' must be >= {min}.");
        }
        if (validation?.MaxValue is decimal max && number > max)
        {
            result.AddError("FIELD_OUT_OF_RANGE", $"Field '{field.Key}' must be <= {max}.");
        }
    }

    private static void ValidateText(FormFieldDefinition field, JsonElement value, WorkflowValidationResult result)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            result.AddError("FIELD_TYPE_MISMATCH", $"Field '{field.Key}' must be a string.");
            return;
        }

        var text = value.GetString() ?? string.Empty;
        var validation = field.Validation;
        if (validation?.MinLength is int minLength && text.Length < minLength)
        {
            result.AddError("FIELD_OUT_OF_RANGE", $"Field '{field.Key}' must be at least {minLength} characters.");
        }
        if (validation?.MaxLength is int maxLength && text.Length > maxLength)
        {
            result.AddError("FIELD_OUT_OF_RANGE", $"Field '{field.Key}' must be at most {maxLength} characters.");
        }
        if (validation?.Pattern is string pattern && !System.Text.RegularExpressions.Regex.IsMatch(text, pattern))
        {
            result.AddError("FIELD_PATTERN_MISMATCH", $"Field '{field.Key}' does not match the required pattern.");
        }
    }

    private static void ValidateOption(FormFieldDefinition field, JsonElement value, WorkflowValidationResult result)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            result.AddError("FIELD_TYPE_MISMATCH", $"Field '{field.Key}' must be a string.");
            return;
        }

        var selected = value.GetString();
        if (field.Options is null || field.Options.All(o => o.Value != selected))
        {
            result.AddError("INVALID_FIELD_OPTION", $"Field '{field.Key}' has an unrecognized option '{selected}'.");
        }
    }
}
