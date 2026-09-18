using System.Text.Json.Serialization;

namespace BPM.Domain.Forms;

// Phase 5.4.2 — Advanced Form Rules. Additive to the existing FormSchema (Skill-era
// FormFieldVisibility stays exactly as it was; this is a second, more general mechanism living
// alongside it, not a replacement) — FormSchema.Rules is the one place a form's business rules
// live, stored inside the same SchemaJson blob FormSchemaValidator/FormEngine already own. There
// is deliberately no separate "Default" rule type here: FormFieldDefinition.DefaultValue already
// existed (Phase 4) but was never actually applied at runtime — this phase wires that up instead
// of inventing a parallel mechanism (see FormEngine's instance-creation path).
public enum FormRuleType
{
    Visibility,
    Enabled,
    Required,
    Calculated,

    // Forward-compatibility catch-all (Skill.md-style "never silently discard unknown data") for
    // a `type` value this phase doesn't understand yet. A rule that deserializes to Unknown always
    // carries its original JSON in RawJson and is never evaluated or validated — it round-trips
    // byte-for-byte through Save Draft, exactly like an unsupported FormFieldType does today.
    Unknown,
}

public enum FormConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    IsEmpty,
    IsNotEmpty,
}

public enum FormLogicalOperator
{
    And,
    Or,
    Not,
}

// A condition tree: either a leaf comparing one field's current value, or a compound node
// combining child conditions with AND/OR/NOT. Recursive by construction so nested grouping (per
// Phase 5.4.2 §6) needs no separate "depth" concept.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FormFieldCondition), "field")]
[JsonDerivedType(typeof(FormCompoundCondition), "compound")]
public abstract record FormCondition;

public record FormFieldCondition(string Field, FormConditionOperator Operator, string? Value = null) : FormCondition;

public record FormCompoundCondition(FormLogicalOperator Operator, IReadOnlyList<FormCondition> Conditions) : FormCondition;

// One rule: "when Condition is true, Target's Type-state changes" (Visibility/Enabled/Required),
// or "Target's value = Formula, recomputed from other fields" (Calculated — Condition unused).
// Id is a stable, designer-assigned string (not a DB key) so the Designer can track/edit/delete a
// specific rule across re-renders the same way DesignerField.internalId tracks a field.
public record FormRule(
    string Id,
    string Target,
    FormRuleType Type,
    FormCondition? Condition = null,
    string? Formula = null,
    System.Text.Json.JsonElement? RawJson = null);
