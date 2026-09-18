using System.Text.Json;
using BPM.Domain.Forms;

namespace BPM.Workflow.Engine;

// Per-field outcome of rule evaluation. `Required` is the *effective* required-ness (static
// field.Required OR-ed with any matching Required-type rule) — FormDataValidator enforces this,
// not field.Required alone, so a client cannot dodge a conditionally-required field simply because
// it looks statically optional in the schema (Phase 5.4.2 §12).
public sealed record EvaluatedFieldState(bool Visible, bool Enabled, bool Required);

public sealed class EvaluatedFormState
{
    public required IReadOnlyDictionary<string, EvaluatedFieldState> Fields { get; init; }

    // The submitted data, with every Calculated field's value overwritten by what the engine
    // itself computed (Phase 5.4.2 §14: never trust a client-submitted calculated value).
    public required JsonElement Data { get; init; }

    // fieldKey -> reason code (currently only "DIVISION_BY_ZERO") for a Calculated field that
    // could not be safely evaluated. Non-empty means the caller must fail closed rather than
    // persist/accept the data as-is.
    public required IReadOnlyDictionary<string, string> CalculationErrors { get; init; }
}

// Deterministic, pure, DB-free runtime evaluator for FormSchema.Rules (Phase 5.4.2 §10) — the
// authoritative counterpart to the frontend's own rule evaluation (Designer Preview / live UX),
// which exists purely for immediate feedback and is never trusted (§11/§12). No UI concerns here,
// no framework dependency beyond System.Text.Json for reading/writing the data blob FormEngine
// already passes around.
public static class FormRuleEngine
{
    public static EvaluatedFormState Evaluate(FormSchema schema, JsonElement data)
    {
        var fieldsByKey = schema.Fields.ToDictionary(f => f.Key, f => f);
        var values = new Dictionary<string, JsonElement>();
        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in data.EnumerateObject())
            {
                values[prop.Name] = prop.Value;
            }
        }

        var rules = (schema.Rules ?? Array.Empty<FormRule>()).Where(r => r.Type != FormRuleType.Unknown).ToList();
        var calculationErrors = new Dictionary<string, string>();

        EvaluateCalculatedFields(schema, fieldsByKey, rules, values, calculationErrors);

        var fieldStates = new Dictionary<string, EvaluatedFieldState>();
        foreach (var field in schema.Fields)
        {
            var visible = EvaluateLegacyVisibility(field, fieldsByKey, values) && EvaluateRuleGroup(rules, field.Key, FormRuleType.Visibility, fieldsByKey, values, defaultWhenNoRules: true);
            var enabled = EvaluateRuleGroup(rules, field.Key, FormRuleType.Enabled, fieldsByKey, values, defaultWhenNoRules: true);
            var conditionallyRequired = EvaluateRuleGroup(rules, field.Key, FormRuleType.Required, fieldsByKey, values, defaultWhenNoRules: false);
            fieldStates[field.Key] = new EvaluatedFieldState(visible, enabled, field.Required || conditionallyRequired);
        }

        return new EvaluatedFormState
        {
            Fields = fieldStates,
            Data = BuildJson(values),
            CalculationErrors = calculationErrors,
        };
    }

    // OR across every rule of `type` targeting `fieldKey`: if any matches, the group's outcome is
    // true. A field with no rules of this type at all gets `defaultWhenNoRules` (true for
    // Visibility/Enabled — "no opinion means normal"; false for Required — "no opinion means not
    // conditionally required," on top of whatever field.Required already says statically).
    private static bool EvaluateRuleGroup(List<FormRule> rules, string fieldKey, FormRuleType type, IReadOnlyDictionary<string, FormFieldDefinition> fieldsByKey, IReadOnlyDictionary<string, JsonElement> values, bool defaultWhenNoRules)
    {
        var matching = rules.Where(r => r.Type == type && r.Target == fieldKey).ToList();
        if (matching.Count == 0)
        {
            return defaultWhenNoRules;
        }
        return matching.Any(r => r.Condition is not null && EvaluateCondition(r.Condition, fieldsByKey, values));
    }

    private static bool EvaluateLegacyVisibility(FormFieldDefinition field, IReadOnlyDictionary<string, FormFieldDefinition> fieldsByKey, IReadOnlyDictionary<string, JsonElement> values)
    {
        if (field.Visibility is null)
        {
            return true;
        }
        var when = field.Visibility.When;
        var actual = GetString(values, when.Field);
        var matches = string.Equals(actual, when.Value, StringComparison.Ordinal);
        return when.Operator == FormVisibilityOperator.Equals ? matches : !matches;
    }

    private static bool EvaluateCondition(FormCondition condition, IReadOnlyDictionary<string, FormFieldDefinition> fieldsByKey, IReadOnlyDictionary<string, JsonElement> values)
    {
        switch (condition)
        {
            case FormFieldCondition leaf:
                return EvaluateLeaf(leaf, fieldsByKey, values);

            case FormCompoundCondition compound:
                return compound.Operator switch
                {
                    FormLogicalOperator.And => compound.Conditions.All(c => EvaluateCondition(c, fieldsByKey, values)),
                    FormLogicalOperator.Or => compound.Conditions.Any(c => EvaluateCondition(c, fieldsByKey, values)),
                    FormLogicalOperator.Not => compound.Conditions.Count == 1 && !EvaluateCondition(compound.Conditions[0], fieldsByKey, values),
                    _ => false,
                };

            default:
                return false;
        }
    }

    private static bool EvaluateLeaf(FormFieldCondition leaf, IReadOnlyDictionary<string, FormFieldDefinition> fieldsByKey, IReadOnlyDictionary<string, JsonElement> values)
    {
        var isEmpty = IsEmpty(values, leaf.Field);
        if (leaf.Operator == FormConditionOperator.IsEmpty) return isEmpty;
        if (leaf.Operator == FormConditionOperator.IsNotEmpty) return !isEmpty;
        if (isEmpty) return false; // every other operator needs an actual value to compare

        var isNumeric = fieldsByKey.TryGetValue(leaf.Field, out var field) && field.Type is FormFieldType.Number or FormFieldType.Currency;

        if (isNumeric && decimal.TryParse(leaf.Value, System.Globalization.CultureInfo.InvariantCulture, out var expectedNum))
        {
            var actualNum = GetDecimal(values, leaf.Field);
            if (actualNum is null) return false;
            return leaf.Operator switch
            {
                FormConditionOperator.Equals => actualNum == expectedNum,
                FormConditionOperator.NotEquals => actualNum != expectedNum,
                FormConditionOperator.GreaterThan => actualNum > expectedNum,
                FormConditionOperator.GreaterThanOrEqual => actualNum >= expectedNum,
                FormConditionOperator.LessThan => actualNum < expectedNum,
                FormConditionOperator.LessThanOrEqual => actualNum <= expectedNum,
                _ => false,
            };
        }

        var actual = GetString(values, leaf.Field) ?? string.Empty;
        var expected = leaf.Value ?? string.Empty;
        return leaf.Operator switch
        {
            FormConditionOperator.Equals => string.Equals(actual, expected, StringComparison.Ordinal),
            FormConditionOperator.NotEquals => !string.Equals(actual, expected, StringComparison.Ordinal),
            FormConditionOperator.Contains => actual.Contains(expected, StringComparison.Ordinal),
            FormConditionOperator.NotContains => !actual.Contains(expected, StringComparison.Ordinal),
            _ => false,
        };
    }

    // Calculated fields may depend on other calculated fields — evaluate in dependency order
    // (Kahn's algorithm over the Calculated-only subgraph). A cycle here is a defensive fallback
    // only: FormSchemaValidator/FormRuleValidator already reject cycles before a schema can be
    // published, but a still-being-edited Draft is not guaranteed valid, and Save Draft must never
    // hang or stack-overflow on bad input — an involved field is simply left unevaluated rather
    // than looped on (§16: "do not attempt uncontrolled recursive evaluation").
    private static void EvaluateCalculatedFields(FormSchema schema, IReadOnlyDictionary<string, FormFieldDefinition> fieldsByKey, List<FormRule> rules, Dictionary<string, JsonElement> values, Dictionary<string, string> calculationErrors)
    {
        var calcRules = rules.Where(r => r.Type == FormRuleType.Calculated && fieldsByKey.ContainsKey(r.Target)).ToList();
        var parsed = new Dictionary<string, FormulaNode>();
        var dependsOn = new Dictionary<string, HashSet<string>>();

        foreach (var rule in calcRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Formula) || !FormFormulaEvaluator.TryParse(rule.Formula, out var node, out _))
            {
                continue;
            }
            parsed[rule.Target] = node!;
            dependsOn[rule.Target] = FormFormulaEvaluator.ReferencedFields(node!)
                .Where(f => calcRules.Any(r => r.Target == f))
                .ToHashSet();
        }

        var order = TopologicalOrder(parsed.Keys, dependsOn);

        foreach (var target in order)
        {
            var node = parsed[target];
            var lookup = new Dictionary<string, decimal?>();
            foreach (var refField in FormFormulaEvaluator.ReferencedFields(node))
            {
                lookup[refField] = GetDecimal(values, refField);
            }

            var evalResult = FormFormulaEvaluator.Evaluate(node, lookup);
            if (evalResult.Error is not null)
            {
                calculationErrors[target] = evalResult.Error;
                continue;
            }
            if (evalResult.Value is decimal computed)
            {
                values[target] = JsonSerializer.SerializeToElement(computed);
            }
        }
    }

    private static List<string> TopologicalOrder(IEnumerable<string> nodes, Dictionary<string, HashSet<string>> dependsOn)
    {
        var result = new List<string>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>();

        void Visit(string node)
        {
            if (visited.Contains(node) || !visiting.Add(node))
            {
                return; // already handled, or a cycle — skip rather than loop
            }
            if (dependsOn.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps) Visit(dep);
            }
            visiting.Remove(node);
            visited.Add(node);
            result.Add(node);
        }

        foreach (var node in nodes) Visit(node);
        return result;
    }

    private static bool IsEmpty(IReadOnlyDictionary<string, JsonElement> values, string key)
    {
        if (!values.TryGetValue(key, out var v)) return true;
        return v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            JsonValueKind.String => string.IsNullOrEmpty(v.GetString()),
            JsonValueKind.Array => v.GetArrayLength() == 0,
            _ => false,
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> values, string key)
    {
        if (!values.TryGetValue(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static decimal? GetDecimal(IReadOnlyDictionary<string, JsonElement> values, string key)
    {
        if (!values.TryGetValue(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var sd)) return sd;
        return null;
    }

    private static JsonElement BuildJson(Dictionary<string, JsonElement> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in values)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
}
