using System.Text.Json;
using BPM.Domain.Forms;
using BPM.Workflow.Engine;
using Xunit;

namespace BPM.Tests.Workflow;

public class FormRuleEngineTests
{
    private static FormFieldDefinition Text(string key, bool required = false) => new(key, FormFieldType.Text, key, Required: required);
    private static FormFieldDefinition Number(string key) => new(key, FormFieldType.Number, key);
    private static FormFieldDefinition Select(string key, params string[] values) =>
        new(key, FormFieldType.Select, key, Options: values.Select(v => new FormFieldOption(v, v)).ToList());

    private static FormFieldCondition Eq(string field, string value) => new(field, FormConditionOperator.Equals, value);

    private static JsonElement Data(object obj) => JsonSerializer.SerializeToElement(obj, FormJson.Options);

    [Theory]
    [InlineData(FormConditionOperator.Equals, "BUSINESS", true)]
    [InlineData(FormConditionOperator.NotEquals, "BUSINESS", false)]
    [InlineData(FormConditionOperator.Contains, "SIN", true)]
    [InlineData(FormConditionOperator.NotContains, "SIN", false)]
    public void Evaluate_LeafConditionOperators_TextField(FormConditionOperator op, string value, bool expectedVisible)
    {
        var schema = new FormSchema(
            new[] { Text("travelType"), Text("destination") },
            new[] { new FormRule("r1", "destination", FormRuleType.Visibility, new FormFieldCondition("travelType", op, value)) });

        var state = FormRuleEngine.Evaluate(schema, Data(new { travelType = "BUSINESS" }));

        Assert.Equal(expectedVisible, state.Fields["destination"].Visible);
    }

    [Theory]
    [InlineData(FormConditionOperator.GreaterThan, "50", true)]
    [InlineData(FormConditionOperator.GreaterThanOrEqual, "100", true)]
    [InlineData(FormConditionOperator.LessThan, "50", false)]
    [InlineData(FormConditionOperator.LessThanOrEqual, "99", false)]
    public void Evaluate_LeafConditionOperators_NumberField(FormConditionOperator op, string value, bool expectedVisible)
    {
        var schema = new FormSchema(
            new[] { Number("amount"), Text("note") },
            new[] { new FormRule("r1", "note", FormRuleType.Visibility, new FormFieldCondition("amount", op, value)) });

        var state = FormRuleEngine.Evaluate(schema, Data(new { amount = 100 }));

        Assert.Equal(expectedVisible, state.Fields["note"].Visible);
    }

    // Rule: show "note" when "comment" IsEmpty. comment="" -> condition true -> note visible.
    // comment="x" -> condition false -> note hidden.
    [Theory]
    [InlineData("", true)]
    [InlineData("x", false)]
    public void Evaluate_IsEmptyIsNotEmpty(string value, bool expectedVisible)
    {
        var schema = new FormSchema(
            new[] { Text("comment"), Text("note") },
            new[] { new FormRule("r1", "note", FormRuleType.Visibility, new FormFieldCondition("comment", FormConditionOperator.IsEmpty)) });

        var state = FormRuleEngine.Evaluate(schema, Data(new { comment = value }));

        Assert.Equal(expectedVisible, state.Fields["note"].Visible);
    }

    [Fact]
    public void Evaluate_CompoundAnd_BothTrue_ResultTrue()
    {
        var condition = new FormCompoundCondition(FormLogicalOperator.And, new FormCondition[]
        {
            new FormFieldCondition("amount", FormConditionOperator.GreaterThan, "100"),
            Eq("expenseType", "PURCHASE"),
        });
        var schema = new FormSchema(
            new[] { Number("amount"), Select("expenseType", "PURCHASE", "TRAVEL"), Text("approverNote") },
            new[] { new FormRule("r1", "approverNote", FormRuleType.Required, condition) });

        var state = FormRuleEngine.Evaluate(schema, Data(new { amount = 150, expenseType = "PURCHASE" }));
        Assert.True(state.Fields["approverNote"].Required);

        var state2 = FormRuleEngine.Evaluate(schema, Data(new { amount = 50, expenseType = "PURCHASE" }));
        Assert.False(state2.Fields["approverNote"].Required);
    }

    [Fact]
    public void Evaluate_CompoundOr_EitherTrue_ResultTrue()
    {
        var condition = new FormCompoundCondition(FormLogicalOperator.Or, new FormCondition[]
        {
            Eq("expenseType", "OTHER"),
            Eq("expenseType", "MISC"),
        });
        var schema = new FormSchema(
            new[] { Select("expenseType", "OTHER", "MISC", "TRAVEL"), Text("description") },
            new[] { new FormRule("r1", "description", FormRuleType.Required, condition) });

        var state = FormRuleEngine.Evaluate(schema, Data(new { expenseType = "MISC" }));
        Assert.True(state.Fields["description"].Required);
    }

    [Fact]
    public void Evaluate_CompoundNot_InvertsResult()
    {
        var condition = new FormCompoundCondition(FormLogicalOperator.Not, new FormCondition[] { Eq("expenseType", "TRAVEL") });
        var schema = new FormSchema(
            new[] { Select("expenseType", "OTHER", "TRAVEL"), Text("description") },
            new[] { new FormRule("r1", "description", FormRuleType.Visibility, condition) });

        var state = FormRuleEngine.Evaluate(schema, Data(new { expenseType = "TRAVEL" }));
        Assert.False(state.Fields["description"].Visible);

        var state2 = FormRuleEngine.Evaluate(schema, Data(new { expenseType = "OTHER" }));
        Assert.True(state2.Fields["description"].Visible);
    }

    [Fact]
    public void Evaluate_EnabledRule_TrueWhenConditionMatches()
    {
        var schema = new FormSchema(
            new[] { Select("status", "APPROVED", "PENDING"), Text("approvalComment") },
            new[] { new FormRule("r1", "approvalComment", FormRuleType.Enabled, Eq("status", "APPROVED")) });

        var enabledState = FormRuleEngine.Evaluate(schema, Data(new { status = "APPROVED" }));
        Assert.True(enabledState.Fields["approvalComment"].Enabled);

        var disabledState = FormRuleEngine.Evaluate(schema, Data(new { status = "PENDING" }));
        Assert.False(disabledState.Fields["approvalComment"].Enabled);
    }

    [Fact]
    public void Evaluate_ConditionalRequired_TrueOnlyWhenConditionMatches()
    {
        var schema = new FormSchema(
            new[] { Select("expenseType", "OTHER", "TRAVEL"), Text("description") },
            new[] { new FormRule("r1", "description", FormRuleType.Required, Eq("expenseType", "OTHER")) });

        var required = FormRuleEngine.Evaluate(schema, Data(new { expenseType = "OTHER" }));
        Assert.True(required.Fields["description"].Required);

        var optional = FormRuleEngine.Evaluate(schema, Data(new { expenseType = "TRAVEL" }));
        Assert.False(optional.Fields["description"].Required);
    }

    [Fact]
    public void Evaluate_StaticallyRequiredField_StaysRequiredRegardlessOfRules()
    {
        var schema = new FormSchema(new[] { Text("name", required: true) });

        var state = FormRuleEngine.Evaluate(schema, Data(new { }));

        Assert.True(state.Fields["name"].Required);
    }

    [Fact]
    public void Evaluate_NoVisibilityOrEnabledRules_DefaultsToVisibleAndEnabled()
    {
        var schema = new FormSchema(new[] { Text("plain") });

        var state = FormRuleEngine.Evaluate(schema, Data(new { }));

        Assert.True(state.Fields["plain"].Visible);
        Assert.True(state.Fields["plain"].Enabled);
        Assert.False(state.Fields["plain"].Required);
    }

    [Fact]
    public void Evaluate_CalculatedField_ComputesAndOverwritesSubmittedValue()
    {
        var schema = new FormSchema(
            new[] { Number("quantity"), Number("unitPrice"), Number("total") },
            new[] { new FormRule("r1", "total", FormRuleType.Calculated, Formula: "quantity * unitPrice") });

        // Client tries to submit a spoofed total — the engine must not trust it.
        var state = FormRuleEngine.Evaluate(schema, Data(new { quantity = 3, unitPrice = 100, total = 999999 }));

        Assert.True(state.Data.TryGetProperty("total", out var totalEl));
        Assert.Equal(300m, totalEl.GetDecimal());
        Assert.Empty(state.CalculationErrors);
    }

    [Fact]
    public void Evaluate_ChainedCalculatedFields_EvaluateInDependencyOrder()
    {
        var schema = new FormSchema(
            new[] { Number("quantity"), Number("unitPrice"), Number("subtotal"), Number("tax"), Number("total") },
            new[]
            {
                new FormRule("r1", "subtotal", FormRuleType.Calculated, Formula: "quantity * unitPrice"),
                new FormRule("r2", "total", FormRuleType.Calculated, Formula: "subtotal + tax"),
            });

        var state = FormRuleEngine.Evaluate(schema, Data(new { quantity = 2, unitPrice = 50, tax = 10 }));

        Assert.Equal(100m, state.Data.GetProperty("subtotal").GetDecimal());
        Assert.Equal(110m, state.Data.GetProperty("total").GetDecimal());
    }

    [Fact]
    public void Evaluate_DivisionByZero_ReportsCalculationError_DoesNotThrow()
    {
        var schema = new FormSchema(
            new[] { Number("amount"), Number("divisor"), Number("result") },
            new[] { new FormRule("r1", "result", FormRuleType.Calculated, Formula: "amount / divisor") });

        var state = FormRuleEngine.Evaluate(schema, Data(new { amount = 10, divisor = 0 }));

        Assert.True(state.CalculationErrors.ContainsKey("result"));
        Assert.Equal("DIVISION_BY_ZERO", state.CalculationErrors["result"]);
    }

    [Fact]
    public void Evaluate_MissingRequiredFieldIsHiddenButStillRequired_NoBypass()
    {
        // §12's own scenario: a field is conditionally hidden AND conditionally required by the
        // same condition — the engine must not let "hidden" quietly exempt "required."
        var schema = new FormSchema(
            new[] { Select("expenseType", "OTHER", "TRAVEL"), Text("description") },
            new[]
            {
                new FormRule("r1", "description", FormRuleType.Visibility, Eq("expenseType", "OTHER")),
                new FormRule("r2", "description", FormRuleType.Required, Eq("expenseType", "OTHER")),
            });

        var state = FormRuleEngine.Evaluate(schema, Data(new { expenseType = "OTHER" }));

        Assert.True(state.Fields["description"].Visible);
        Assert.True(state.Fields["description"].Required);
    }
}
