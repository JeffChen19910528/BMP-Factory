using BPM.Workflow.Engine;
using Xunit;

namespace BPM.Tests.Workflow;

public class FormFormulaEvaluatorTests
{
    [Fact]
    public void TryParse_SimpleMultiplication_Succeeds()
    {
        Assert.True(FormFormulaEvaluator.TryParse("quantity * unitPrice", out var node, out var error));
        Assert.Null(error);
        Assert.Equal(new HashSet<string> { "quantity", "unitPrice" }, FormFormulaEvaluator.ReferencedFields(node!).ToHashSet());
    }

    [Fact]
    public void Evaluate_QuantityTimesUnitPrice_Returns300()
    {
        FormFormulaEvaluator.TryParse("quantity * unitPrice", out var node, out _);
        var result = FormFormulaEvaluator.Evaluate(node!, new Dictionary<string, decimal?> { ["quantity"] = 3, ["unitPrice"] = 100 });

        Assert.Equal(300m, result.Value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Evaluate_SubtotalPlusTax_WithParentheses_ComputesCorrectly()
    {
        FormFormulaEvaluator.TryParse("(subtotal + tax) * 1", out var node, out var error);
        Assert.Null(error);
        var result = FormFormulaEvaluator.Evaluate(node!, new Dictionary<string, decimal?> { ["subtotal"] = 100, ["tax"] = 8 });

        Assert.Equal(108m, result.Value);
    }

    [Fact]
    public void Evaluate_DivisionByZero_FailsSafely()
    {
        FormFormulaEvaluator.TryParse("amount / divisor", out var node, out _);
        var result = FormFormulaEvaluator.Evaluate(node!, new Dictionary<string, decimal?> { ["amount"] = 10, ["divisor"] = 0 });

        Assert.Null(result.Value);
        Assert.Equal("DIVISION_BY_ZERO", result.Error);
    }

    [Fact]
    public void Evaluate_MissingFieldValue_ReturnsNullWithoutError()
    {
        FormFormulaEvaluator.TryParse("quantity * unitPrice", out var node, out _);
        var result = FormFormulaEvaluator.Evaluate(node!, new Dictionary<string, decimal?> { ["quantity"] = 3 });

        Assert.Null(result.Value);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("quantity * ")]
    [InlineData("quantity ** unitPrice")]
    [InlineData("quantity + (unitPrice")]
    [InlineData("quantity; unitPrice")]
    [InlineData("eval(quantity)")]
    public void TryParse_InvalidSyntax_Rejected(string formula)
    {
        // "eval(quantity)" is deliberately included: it must be rejected as an ordinary field
        // reference followed by an unexpected '(' — there is no function-call syntax at all, so a
        // name that merely spells "eval" carries no special meaning and cannot execute anything.
        var ok = FormFormulaEvaluator.TryParse(formula, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_UnsupportedCharacter_Rejected()
    {
        Assert.False(FormFormulaEvaluator.TryParse("quantity ^ 2", out _, out var error));
        Assert.Contains("Unsupported character", error);
    }
}
