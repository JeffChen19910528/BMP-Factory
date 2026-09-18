using BPM.Domain.Forms;
using BPM.Workflow.Validation;
using Xunit;

namespace BPM.Tests.Workflow;

public class FormRuleValidatorTests
{
    private static FormFieldDefinition Text(string key) => new(key, FormFieldType.Text, key);
    private static FormFieldDefinition Number(string key) => new(key, FormFieldType.Number, key);
    private static FormFieldDefinition Select(string key, params string[] values) =>
        new(key, FormFieldType.Select, key, Options: values.Select(v => new FormFieldOption(v, v)).ToList());

    private static FormFieldCondition Eq(string field, string value) => new(field, FormConditionOperator.Equals, value);

    private static FormSchema TravelSchema(IReadOnlyList<FormRule> rules) => new(
        new[]
        {
            Select("travelType", "BUSINESS", "PERSONAL"),
            Text("destination"),
            Number("amount"),
        },
        rules);

    [Fact]
    public void Validate_ValidVisibilityRule_Passes()
    {
        var schema = TravelSchema(new[]
        {
            new FormRule("r1", "destination", FormRuleType.Visibility, Eq("travelType", "BUSINESS")),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_DuplicateRuleId_Rejected()
    {
        var schema = TravelSchema(new[]
        {
            new FormRule("dup", "destination", FormRuleType.Visibility, Eq("travelType", "BUSINESS")),
            new FormRule("dup", "amount", FormRuleType.Required, Eq("travelType", "BUSINESS")),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "DUPLICATE_RULE_ID");
    }

    [Fact]
    public void Validate_MissingRuleTarget_Rejected()
    {
        var schema = TravelSchema(new[]
        {
            new FormRule("r1", "doesNotExist", FormRuleType.Visibility, Eq("travelType", "BUSINESS")),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_RULE_TARGET");
    }

    [Fact]
    public void Validate_MissingSourceField_Rejected()
    {
        var schema = TravelSchema(new[]
        {
            new FormRule("r1", "destination", FormRuleType.Visibility, Eq("doesNotExist", "BUSINESS")),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_RULE_CONDITION_FIELD");
    }

    [Fact]
    public void Validate_SelfDependency_Rejected()
    {
        var schema = TravelSchema(new[]
        {
            new FormRule("r1", "destination", FormRuleType.Visibility, Eq("destination", "x")),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_RULE_SELF_DEPENDENCY");
    }

    [Fact]
    public void Validate_OperatorNotValidForFieldType_Rejected()
    {
        // "greaterThan" is not a valid operator for a Text field.
        var schema = TravelSchema(new[]
        {
            new FormRule("r1", "amount", FormRuleType.Required, new FormFieldCondition("destination", FormConditionOperator.GreaterThan, "100")),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_RULE_OPERATOR");
    }

    [Fact]
    public void Validate_IncompatibleValueForSelectField_Rejected()
    {
        var schema = TravelSchema(new[]
        {
            new FormRule("r1", "destination", FormRuleType.Visibility, Eq("travelType", "NOT_AN_OPTION")),
        });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_RULE_VALUE");
    }

    [Fact]
    public void Validate_CircularDependency_Rejected()
    {
        var schema = new FormSchema(
            new[] { Text("a"), Text("b"), Text("c") },
            new[]
            {
                new FormRule("r1", "a", FormRuleType.Visibility, Eq("b", "x")),
                new FormRule("r2", "b", FormRuleType.Visibility, Eq("c", "x")),
                new FormRule("r3", "c", FormRuleType.Visibility, Eq("a", "x")),
            });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "CIRCULAR_RULE_DEPENDENCY");
    }

    [Fact]
    public void Validate_CompoundAndOrConditions_Pass()
    {
        var condition = new FormCompoundCondition(FormLogicalOperator.And, new FormCondition[]
        {
            new FormFieldCondition("amount", FormConditionOperator.GreaterThan, "100"),
            new FormCompoundCondition(FormLogicalOperator.Or, new FormCondition[]
            {
                Eq("travelType", "BUSINESS"),
                Eq("travelType", "PERSONAL"),
            }),
        });
        var schema = TravelSchema(new[] { new FormRule("r1", "destination", FormRuleType.Visibility, condition) });

        var result = FormSchemaValidator.Validate(schema);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NotConditionWithMoreThanOneChild_Rejected()
    {
        var condition = new FormCompoundCondition(FormLogicalOperator.Not, new FormCondition[]
        {
            Eq("travelType", "BUSINESS"),
            Eq("travelType", "PERSONAL"),
        });
        var schema = TravelSchema(new[] { new FormRule("r1", "destination", FormRuleType.Visibility, condition) });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_RULE_CONDITION");
    }

    [Fact]
    public void Validate_CalculatedField_ValidFormula_Passes()
    {
        var schema = new FormSchema(
            new[] { Number("quantity"), Number("unitPrice"), Number("total") },
            new[] { new FormRule("r1", "total", FormRuleType.Calculated, Formula: "quantity * unitPrice") });

        var result = FormSchemaValidator.Validate(schema);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_CalculatedField_InvalidFormula_Rejected()
    {
        var schema = new FormSchema(
            new[] { Number("quantity"), Number("total") },
            new[] { new FormRule("r1", "total", FormRuleType.Calculated, Formula: "quantity * ") });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_RULE_FORMULA");
    }

    [Fact]
    public void Validate_CalculatedField_MissingFormula_Rejected()
    {
        var schema = new FormSchema(
            new[] { Number("quantity"), Number("total") },
            new[] { new FormRule("r1", "total", FormRuleType.Calculated) });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "MISSING_RULE_FORMULA");
    }

    [Fact]
    public void Validate_CalculatedField_SelfReferencingFormula_Rejected()
    {
        var schema = new FormSchema(
            new[] { Number("total") },
            new[] { new FormRule("r1", "total", FormRuleType.Calculated, Formula: "total * 2") });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_RULE_SELF_DEPENDENCY");
    }

    [Fact]
    public void Validate_CalculatedField_NonNumericTarget_Rejected()
    {
        var schema = new FormSchema(
            new[] { Number("quantity"), Text("label") },
            new[] { new FormRule("r1", "label", FormRuleType.Calculated, Formula: "quantity * 2") });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "INVALID_RULE_TARGET");
    }

    [Fact]
    public void Validate_MissingConditionOnConditionalRule_Rejected()
    {
        var schema = TravelSchema(new[] { new FormRule("r1", "destination", FormRuleType.Visibility) });

        var result = FormSchemaValidator.Validate(schema);

        Assert.Contains(result.Errors, e => e.Code == "MISSING_RULE_CONDITION");
    }

    [Fact]
    public void Validate_UnknownRuleType_PreservedAndNeverRejected()
    {
        var raw = System.Text.Json.JsonDocument.Parse("""{"id":"future1","target":"destination","type":"futureRule","someNewShape":true}""").RootElement;
        var schema = TravelSchema(new[] { new FormRule("future1", "destination", FormRuleType.Unknown, RawJson: raw) });

        var result = FormSchemaValidator.Validate(schema);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_UnknownRuleType_RoundTripsRawJsonVerbatim()
    {
        var raw = System.Text.Json.JsonDocument.Parse("""{"id":"future1","target":"destination","type":"futureRule","someNewShape":true}""").RootElement;
        var schema = TravelSchema(new[] { new FormRule("future1", "destination", FormRuleType.Unknown, RawJson: raw) });

        var json = FormJson.Serialize(schema);
        var roundTripped = FormJson.TryDeserialize(json);

        Assert.NotNull(roundTripped);
        var rule = Assert.Single(roundTripped!.Rules!);
        Assert.Equal(FormRuleType.Unknown, rule.Type);
        Assert.Contains("someNewShape", json);
    }
}
