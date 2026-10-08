using Nina.Family.Persistence;
using Nina.Family.Services;

namespace Nina.Family.Tests.Unit;

/// <summary>RF-005: idade em dias/semanas/meses completos, derivada na data local do bebê.</summary>
public sealed class AgeTests
{
    private static BabyRow Row(DateOnly birth, DateOnly? due, DateOnly asOf, int? corrected, bool applied) => new(
        Guid.NewGuid(), "Nina", birth, due, null, "UTC", null, "OWNER", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        asOf, asOf.DayNumber - birth.DayNumber, corrected, applied);

    [Theory]
    [InlineData("2026-01-10", "2026-04-10", 3)]
    [InlineData("2026-01-10", "2026-04-09", 2)]
    [InlineData("2026-01-10", "2026-10-08", 8)]
    [InlineData("2026-01-31", "2026-02-28", 0)]
    [InlineData("2026-01-10", "2026-01-10", 0)]
    [InlineData("2026-01-10", "2025-12-01", 0)]
    [InlineData("2025-01-10", "2026-01-10", 12)]
    public void Whole_months_are_complete_calendar_months(string from, string to, int expected) =>
        Assert.Equal(expected, BabyService.WholeMonths(DateOnly.Parse(from), DateOnly.Parse(to)));

    [Fact]
    public void RF_005_A1_ninety_days_is_12_weeks_and_3_months_and_without_due_date_there_is_no_corrected_age()
    {
        var dto = BabyService.ToDto(Row(new DateOnly(2026, 1, 10), null, new DateOnly(2026, 4, 10), null, false));
        Assert.Equal(90, dto.AgeCalculation.ChronologicalDays);
        Assert.Equal(12, dto.Age.Chronological.Weeks);
        Assert.Equal(3, dto.Age.Chronological.Months);
        Assert.Null(dto.Age.Corrected);
        Assert.Null(dto.AgeCalculation.CorrectedDays);
        Assert.False(dto.AgeCalculation.CorrectionApplied);
        Assert.Equal("CHRONOLOGICAL", dto.Age.Displayed);
    }

    [Fact]
    public void Contract_example_chronological_271_days_and_corrected_257_days()
    {
        var dto = BabyService.ToDto(Row(new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 24), new DateOnly(2026, 10, 8), 257, true));
        Assert.Equal(271, dto.AgeCalculation.ChronologicalDays);
        Assert.Equal(38, dto.Age.Chronological.Weeks);
        Assert.Equal(8, dto.Age.Chronological.Months);
        Assert.Equal(257, dto.AgeCalculation.CorrectedDays);
        Assert.Equal(36, dto.Age.Corrected!.Weeks);
        Assert.Equal(8, dto.Age.Corrected.Months);
        Assert.Equal("CORRECTED", dto.Age.Displayed);
    }

    [Fact]
    public void A_baby_not_yet_at_term_has_negative_corrected_days_and_zero_corrected_months()
    {
        var dto = BabyService.ToDto(Row(new DateOnly(2026, 1, 10), new DateOnly(2026, 2, 10), new DateOnly(2026, 1, 20), -21, true));
        Assert.Equal(-21, dto.AgeCalculation.CorrectedDays);
        Assert.Equal(0, dto.Age.Corrected!.Months);
        Assert.Equal(-3, dto.Age.Corrected.Weeks);
    }
}
