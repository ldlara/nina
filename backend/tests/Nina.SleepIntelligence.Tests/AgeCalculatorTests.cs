namespace Nina.SleepIntelligence.Tests;

public sealed class AgeCalculatorTests
{
    private static readonly DateOnly Birth = new(2026, 1, 10);

    [Fact]
    public void Without_due_date_correction_does_not_apply()
    {
        var a = AgeCalculator.Calculate(Birth, null, new DateOnly(2026, 4, 10));
        Assert.Equal(90, a.ChronologicalDays);
        Assert.Null(a.CorrectedDays);
        Assert.False(a.CorrectionApplied);
        Assert.Equal(90, a.EffectiveDays);
        Assert.Equal("CHRONOLOGICAL", a.Basis);
    }

    [Fact]
    public void Premature_baby_uses_corrected_age()
    {
        var a = AgeCalculator.Calculate(Birth, Birth.AddDays(56), new DateOnly(2026, 4, 10));
        Assert.Equal(90, a.ChronologicalDays);
        Assert.Equal(34, a.CorrectedDays);
        Assert.True(a.CorrectionApplied);
        Assert.Equal(34, a.EffectiveDays);
        Assert.Equal("CORRECTED", a.Basis);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Term_or_late_births_do_not_apply_correction(int dueOffset)
    {
        var a = AgeCalculator.Calculate(Birth, Birth.AddDays(dueOffset), new DateOnly(2026, 4, 10));
        Assert.Null(a.CorrectedDays);
        Assert.False(a.CorrectionApplied);
    }

    [Fact]
    public void Implausible_due_date_or_age_beyond_window_does_not_apply()
    {
        Assert.False(AgeCalculator.Calculate(Birth, Birth.AddDays(400), new DateOnly(2026, 4, 10)).CorrectionApplied);
        Assert.False(AgeCalculator.Calculate(Birth, Birth.AddDays(30), Birth.AddDays(731)).CorrectionApplied);
        Assert.True(AgeCalculator.Calculate(Birth, Birth.AddDays(30), Birth.AddDays(730)).CorrectionApplied);
    }

    [Fact]
    public void Before_due_date_corrected_is_negative_but_effective_age_is_clamped_to_zero()
    {
        var a = AgeCalculator.Calculate(Birth, Birth.AddDays(56), Birth.AddDays(20));
        Assert.Equal(-36, a.CorrectedDays);
        Assert.Equal(0, a.EffectiveDays);
    }

    [Fact]
    public void Age_is_a_pure_function_of_dates_so_editing_dates_recomputes()
    {
        var a = AgeCalculator.Calculate(Birth, Birth.AddDays(56), new DateOnly(2026, 4, 10));
        var b = AgeCalculator.Calculate(Birth, Birth.AddDays(56), new DateOnly(2026, 4, 10));
        Assert.Equal(a, b);
        Assert.NotEqual(a, AgeCalculator.Calculate(Birth, Birth.AddDays(28), new DateOnly(2026, 4, 10)));
    }
}
