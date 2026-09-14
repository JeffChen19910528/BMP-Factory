namespace BPM.Domain.Forms;

// Field types a FormVersion.SchemaJson may contain (Skill.md Phase 4 §7). Text/Textarea/Number/
// Currency/Date/DateTime/Select/Radio/Checkbox/User/Department/File are validated and enforced
// as of Phase 4; RichText/Table/Signature/Formula/Computed are reserved so the schema and
// validator don't need to change shape again when they're implemented later.
public enum FormFieldType
{
    Text,
    Textarea,
    Number,
    Currency,
    Date,
    DateTime,
    Select,
    Radio,
    Checkbox,
    User,
    Department,
    File,
    RichText,
    Table,
    Signature,
    Formula,
    Computed,
}

// Declarative field-level validation (Skill.md §15) — deliberately just data, never an
// expression string: there is no executable-code path here by construction, unlike a
// "formula"-typed field (which is why Formula/Computed stay reserved rather than implemented).
public record FormFieldValidation(
    int? MinLength = null,
    int? MaxLength = null,
    string? Pattern = null,
    decimal? MinValue = null,
    decimal? MaxValue = null);

// Declarative conditional visibility (Skill.md §16): "when <Field> <Operator> <Value>, show this
// field." Intentionally a fixed, tiny operator set — not a general expression engine.
public enum FormVisibilityOperator
{
    Equals,
    NotEquals,
}

public record FormVisibilityCondition(string Field, FormVisibilityOperator Operator, string Value);

public record FormFieldVisibility(FormVisibilityCondition When);

public record FormFieldOption(string Value, string Label);

public record FormFieldDefinition(
    string Key,
    FormFieldType Type,
    string Label,
    string? Description = null,
    bool Required = false,
    string? DefaultValue = null,
    string? Placeholder = null,
    IReadOnlyList<FormFieldOption>? Options = null,
    FormFieldValidation? Validation = null,
    FormFieldVisibility? Visibility = null,
    bool ReadOnly = false);

// Deserialized form of FormVersion.SchemaJson (Skill.md §6). Lives in BPM.Domain (not
// BPM.Workflow), same rationale as BPM.Domain.Workflow.WorkflowDefinition: a dependency-free,
// framework-agnostic shape that both the validator and the engine need to share.
public record FormSchema(IReadOnlyList<FormFieldDefinition> Fields);
