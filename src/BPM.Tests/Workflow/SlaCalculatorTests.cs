using BPM.Application.Common;
using BPM.Workflow.Engine;
using Xunit;

namespace BPM.Tests.Workflow;

// Phase 6.3 — pure, deterministic calculator coverage (Part AA: no DateTime.Now, no real-time
// waiting — every test passes an explicit startedAtUtc).
public class SlaCalculatorTests
{
    private static readonly DateTime StartedAt = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Calculate_ValidInputs_ReturnsCorrectDueAtAndWarningAt()
    {
        var result = SlaCalculator.Calculate(durationMinutes: 24 * 60, warningOffsetMinutes: 12 * 60, StartedAt);

        Assert.Equal(StartedAt, result.StartedAt);
        Assert.Equal(new DateTime(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc), result.DueAt);
        Assert.Equal(new DateTime(2026, 9, 16, 22, 0, 0, DateTimeKind.Utc), result.WarningAt);
    }

    [Fact]
    public void Calculate_ResultIsUtc_MatchingInputKind()
    {
        var result = SlaCalculator.Calculate(60, 30, StartedAt);

        Assert.Equal(DateTimeKind.Utc, result.StartedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, result.DueAt.Kind);
        Assert.Equal(DateTimeKind.Utc, result.WarningAt.Kind);
    }

    [Fact]
    public void Calculate_ZeroDuration_Rejected()
    {
        var ex = Assert.Throws<BadRequestAppException>(() => SlaCalculator.Calculate(0, 0, StartedAt));
        Assert.Equal("INVALID_SLA_DURATION", ex.Code);
    }

    [Fact]
    public void Calculate_NegativeDuration_Rejected()
    {
        var ex = Assert.Throws<BadRequestAppException>(() => SlaCalculator.Calculate(-10, 0, StartedAt));
        Assert.Equal("INVALID_SLA_DURATION", ex.Code);
    }

    [Fact]
    public void Calculate_NegativeWarningOffset_Rejected()
    {
        var ex = Assert.Throws<BadRequestAppException>(() => SlaCalculator.Calculate(60, -1, StartedAt));
        Assert.Equal("INVALID_SLA_WARNING_OFFSET", ex.Code);
    }

    [Fact]
    public void Calculate_WarningOffsetEqualToDuration_Rejected()
    {
        var ex = Assert.Throws<BadRequestAppException>(() => SlaCalculator.Calculate(60, 60, StartedAt));
        Assert.Equal("INVALID_SLA_WARNING_OFFSET", ex.Code);
    }

    [Fact]
    public void Calculate_WarningOffsetGreaterThanDuration_Rejected()
    {
        var ex = Assert.Throws<BadRequestAppException>(() => SlaCalculator.Calculate(60, 90, StartedAt));
        Assert.Equal("INVALID_SLA_WARNING_OFFSET", ex.Code);
    }

    [Fact]
    public void Calculate_ZeroWarningOffset_WarningAtEqualsDueAt()
    {
        var result = SlaCalculator.Calculate(60, 0, StartedAt);
        Assert.Equal(result.DueAt, result.WarningAt);
    }
}
