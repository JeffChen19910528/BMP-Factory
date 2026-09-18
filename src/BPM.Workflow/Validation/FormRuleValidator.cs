using BPM.Domain.Forms;
using BPM.Workflow.Engine;

namespace BPM.Workflow.Validation;

// Phase 5.4.2 — schema-time validation for FormSchema.Rules, called additively from
// FormSchemaValidator.Validate (no signature change there — this is a second internal step, same
// pattern ValidateVisibilityReferences already used for the legacy per-field Visibility). Pure and
// DB-free, fails closed: any rule problem blocks publish, same as every other FormSchemaValidator
// check.
internal static class FormRuleValidator
{
    private static readonly Dictionary<FormFieldType, HashSet<FormConditionOperator>> AllowedOperators = new()
    {
        [FormFieldType.Text] = TextOps(),
        [FormFieldType.Textarea] = TextOps(),
        [FormFieldType.Number] = NumericOps(),
        [FormFieldType.Currency] = NumericOps(),
        [FormFieldType.Date] = NumericOps(),
        [FormFieldType.DateTime] = NumericOps(),
        [FormFieldType.Select] = ChoiceOps(),
        [FormFieldType.Radio] = ChoiceOps(),
        [FormFieldType.Checkbox] = new() { FormConditionOperator.Equals, FormConditionOperator.NotEquals },
        [FormFieldType.User] = ChoiceOps(),
        [FormFieldType.Department] = ChoiceOps(),
        [FormFieldType.File] = new() { FormConditionOperator.IsEmpty, FormConditionOperator.IsNotEmpty },
    };

    private static readonly HashSet<FormConditionOperator> NoValueOperators = new()
    {
        FormConditionOperator.IsEmpty,
        FormConditionOperator.IsNotEmpty,
    };

    private static HashSet<FormConditionOperator> TextOps() => new()
    {
        FormConditionOperator.Equals, FormConditionOperator.NotEquals,
        FormConditionOperator.Contains, FormConditionOperator.NotContains,
        FormConditionOperator.IsEmpty, FormConditionOperator.IsNotEmpty,
    };

    private static HashSet<FormConditionOperator> NumericOps() => new()
    {
        FormConditionOperator.Equals, FormConditionOperator.NotEquals,
        FormConditionOperator.GreaterThan, FormConditionOperator.GreaterThanOrEqual,
        FormConditionOperator.LessThan, FormConditionOperator.LessThanOrEqual,
        FormConditionOperator.IsEmpty, FormConditionOperator.IsNotEmpty,
    };

    private static HashSet<FormConditionOperator> ChoiceOps() => new()
    {
        FormConditionOperator.Equals, FormConditionOperator.NotEquals,
        FormConditionOperator.IsEmpty, FormConditionOperator.IsNotEmpty,
    };

    private static readonly HashSet<FormFieldType> NumericFieldTypes = new() { FormFieldType.Number, FormFieldType.Currency };

    public static void Validate(FormSchema schema, WorkflowValidationResult result)
    {
        var rules = schema.Rules;
        if (rules is null || rules.Count == 0)
        {
            return;
        }

        var fieldsByKey = schema.Fields.ToDictionary(f => f.Key, f => f);
        var seenIds = new HashSet<string>();
        // field key -> set of field keys it depends on (edges point from a rule's Target to
        // whatever it reads), used for cycle detection once all rules have been shape-checked.
        var dependsOn = new Dictionary<string, HashSet<string>>();

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                result.AddError("INVALID_RULE_ID", "Every rule must have a non-empty id.");
            }
            else if (!seenIds.Add(rule.Id))
            {
                result.AddError("DUPLICATE_RULE_ID", $"Rule id '{rule.Id}' is used more than once.");
            }

            if (rule.Type == FormRuleType.Unknown)
            {
                // Forward-compatibility (§18): never evaluated, never further validated, never
                // rejected merely for being unrecognized — but it still occupies its Id above.
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.Target) || !fieldsByKey.ContainsKey(rule.Target))
            {
                result.AddError("INVALID_RULE_TARGET", $"Rule '{rule.Id}' targets unknown field '{rule.Target}'.");
                continue;
            }

            var edges = dependsOn.TryGetValue(rule.Target, out var existing) ? existing : dependsOn[rule.Target] = new HashSet<string>();

            switch (rule.Type)
            {
                case FormRuleType.Visibility:
                case FormRuleType.Enabled:
                case FormRuleType.Required:
                    ValidateConditionRule(rule, fieldsByKey, edges, result);
                    break;

                case FormRuleType.Calculated:
                    ValidateCalculatedRule(rule, fieldsByKey, edges, result);
                    break;
            }
        }

        if (result.IsValid)
        {
            DetectCycles(dependsOn, result);
        }
    }

    private static void ValidateConditionRule(FormRule rule, IReadOnlyDictionary<string, FormFieldDefinition> fieldsByKey, HashSet<string> edges, WorkflowValidationResult result)
    {
        if (rule.Condition is null)
        {
            result.AddError("MISSING_RULE_CONDITION", $"Rule '{rule.Id}' ({rule.Type}) must specify a condition.");
            return;
        }

        ValidateCondition(rule, rule.Condition, fieldsByKey, edges, result);
    }

    private static void ValidateCondition(FormRule rule, FormCondition condition, IReadOnlyDictionary<string, FormFieldDefinition> fieldsByKey, HashSet<string> edges, WorkflowValidationResult result)
    {
        switch (condition)
        {
            case FormFieldCondition leaf:
                ValidateLeafCondition(rule, leaf, fieldsByKey, edges, result);
                break;

            case FormCompoundCondition compound:
                if (compound.Operator == FormLogicalOperator.Not && compound.Conditions.Count != 1)
                {
                    result.AddError("INVALID_RULE_CONDITION", $"Rule '{rule.Id}': a NOT condition must have exactly one child condition.");
                }
                else if (compound.Operator != FormLogicalOperator.Not && compound.Conditions.Count == 0)
                {
                    result.AddError("INVALID_RULE_CONDITION", $"Rule '{rule.Id}': {compound.Operator} must have at least one child condition.");
                }
                foreach (var child in compound.Conditions)
                {
                    ValidateCondition(rule, child, fieldsByKey, edges, result);
                }
                break;
        }
    }

    private static void ValidateLeafCondition(FormRule rule, FormFieldCondition leaf, IReadOnlyDictionary<string, FormFieldDefinition> fieldsByKey, HashSet<string> edges, WorkflowValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(leaf.Field) || !fieldsByKey.TryGetValue(leaf.Field, out var sourceField))
        {
            result.AddError("INVALID_RULE_CONDITION_FIELD", $"Rule '{rule.Id}' references unknown field '{leaf.Field}'.");
            return;
        }

        if (leaf.Field == rule.Target)
        {
            result.AddError("INVALID_RULE_SELF_DEPENDENCY", $"Rule '{rule.Id}' cannot depend on its own target field '{rule.Target}'.");
            return;
        }

        edges.Add(leaf.Field);

        if (!AllowedOperators.TryGetValue(sourceField.Type, out var allowed) || !allowed.Contains(leaf.Operator))
        {
            result.AddError("INVALID_RULE_OPERATOR", $"Rule '{rule.Id}': operator '{leaf.Operator}' is not valid for field '{leaf.Field}' of type '{sourceField.Type}'.");
            return;
        }

        var needsValue = !NoValueOperators.Contains(leaf.Operator);
        if (needsValue && string.IsNullOrEmpty(leaf.Value))
        {
            result.AddError("INVALID_RULE_VALUE", $"Rule '{rule.Id}': operator '{leaf.Operator}' on field '{leaf.Field}' requires a comparison value.");
            return;
        }
        if (!needsValue)
        {
            return;
        }

        if (NumericFieldTypes.Contains(sourceField.Type) && !decimal.TryParse(leaf.Value, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            result.AddError("INVALID_RULE_VALUE", $"Rule '{rule.Id}': value '{leaf.Value}' is not a valid number for field '{leaf.Field}'.");
        }
        else if (sourceField.Type == FormFieldType.Checkbox && !bool.TryParse(leaf.Value, out _))
        {
            result.AddError("INVALID_RULE_VALUE", $"Rule '{rule.Id}': value '{leaf.Value}' is not a valid boolean for field '{leaf.Field}'.");
        }
        else if ((sourceField.Type is FormFieldType.Select or FormFieldType.Radio)
                 && sourceField.Options is not null
                 && sourceField.Options.All(o => o.Value != leaf.Value))
        {
            result.AddError("INVALID_RULE_VALUE", $"Rule '{rule.Id}': value '{leaf.Value}' is not one of field '{leaf.Field}''s options.");
        }
    }

    private static void ValidateCalculatedRule(FormRule rule, IReadOnlyDictionary<string, FormFieldDefinition> fieldsByKey, HashSet<string> edges, WorkflowValidationResult result)
    {
        var target = fieldsByKey[rule.Target];
        if (!NumericFieldTypes.Contains(target.Type))
        {
            result.AddError("INVALID_RULE_TARGET", $"Rule '{rule.Id}': calculated field '{rule.Target}' must be a Number or Currency field.");
        }

        if (string.IsNullOrWhiteSpace(rule.Formula))
        {
            result.AddError("MISSING_RULE_FORMULA", $"Rule '{rule.Id}' (Calculated) must specify a formula.");
            return;
        }

        if (!FormFormulaEvaluator.TryParse(rule.Formula, out var node, out var error))
        {
            result.AddError("INVALID_RULE_FORMULA", $"Rule '{rule.Id}': {error}");
            return;
        }

        foreach (var referenced in FormFormulaEvaluator.ReferencedFields(node!))
        {
            if (referenced == rule.Target)
            {
                result.AddError("INVALID_RULE_SELF_DEPENDENCY", $"Rule '{rule.Id}': formula for '{rule.Target}' cannot reference itself.");
                continue;
            }

            if (!fieldsByKey.TryGetValue(referenced, out var refField))
            {
                result.AddError("INVALID_RULE_CONDITION_FIELD", $"Rule '{rule.Id}': formula references unknown field '{referenced}'.");
                continue;
            }

            if (!NumericFieldTypes.Contains(refField.Type))
            {
                result.AddError("INVALID_RULE_FORMULA", $"Rule '{rule.Id}': formula references non-numeric field '{referenced}'.");
                continue;
            }

            edges.Add(referenced);
        }
    }

    // dependsOn[target] = fields target's rules read from. A cycle here (A depends on B, B depends
    // on A, directly or transitively) would make evaluation order undefined — reject rather than
    // guess at one (§16: "do not attempt uncontrolled recursive evaluation").
    private static void DetectCycles(Dictionary<string, HashSet<string>> dependsOn, WorkflowValidationResult result)
    {
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();

        foreach (var start in dependsOn.Keys)
        {
            if (Visit(start, dependsOn, visiting, visited, out var cyclePath))
            {
                result.AddError("CIRCULAR_RULE_DEPENDENCY", $"Circular rule dependency detected: {string.Join(" -> ", cyclePath!)}.");
                return;
            }
        }
    }

    private static bool Visit(string node, Dictionary<string, HashSet<string>> graph, HashSet<string> visiting, HashSet<string> visited, out List<string>? cyclePath)
    {
        cyclePath = null;
        if (visited.Contains(node))
        {
            return false;
        }
        if (!visiting.Add(node))
        {
            cyclePath = new List<string> { node };
            return true;
        }

        if (graph.TryGetValue(node, out var deps))
        {
            foreach (var dep in deps)
            {
                if (Visit(dep, graph, visiting, visited, out cyclePath))
                {
                    cyclePath!.Insert(0, node);
                    return true;
                }
            }
        }

        visiting.Remove(node);
        visited.Add(node);
        return false;
    }
}
