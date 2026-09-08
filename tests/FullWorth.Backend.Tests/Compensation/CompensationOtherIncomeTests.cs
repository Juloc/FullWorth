using FullWorth.Backend.Modules.Compensation;

namespace FullWorth.Backend.Tests.Compensation;

/// <summary>
/// Pure unit tests for the "sonstige regelmäßige Einkünfte" track — no database, so these run without
/// FULLWORTH_TEST_POSTGRES.
/// </summary>
public sealed class CompensationOtherIncomeTests
{
    [Fact]
    public void RecordInsideItsWindowContributes()
    {
        var records = new[] { Record(300m, new DateOnly(2024, 1, 1), new DateOnly(2026, 12, 31)) };

        var amounts = CompensationOtherIncome.AmountsOn(records, new DateOnly(2025, 6, 1));

        Assert.Equal(300m, amounts.MonthlyTotal);
        Assert.Equal(3_600m, amounts.AnnualTotal);
        Assert.Equal(3_600m, amounts.AnnualCounted);
    }

    [Theory]
    [InlineData(2023, 12, 31)] // one day before "von"
    [InlineData(2027, 1, 1)]   // one day after "bis"
    public void RecordOutsideItsWindowDoesNotContribute(int year, int month, int day)
    {
        var records = new[] { Record(300m, new DateOnly(2024, 1, 1), new DateOnly(2026, 12, 31)) };

        var amounts = CompensationOtherIncome.AmountsOn(records, new DateOnly(year, month, day));

        Assert.Equal(0m, amounts.MonthlyTotal);
        Assert.Equal(0m, amounts.AnnualTotal);
        Assert.Equal(0m, amounts.AnnualCounted);
    }

    [Fact]
    public void WindowBoundariesAreInclusive()
    {
        var records = new[] { Record(300m, new DateOnly(2024, 1, 1), new DateOnly(2026, 12, 31)) };

        Assert.Equal(300m, CompensationOtherIncome.AmountsOn(records, new DateOnly(2024, 1, 1)).MonthlyTotal);
        Assert.Equal(300m, CompensationOtherIncome.AmountsOn(records, new DateOnly(2026, 12, 31)).MonthlyTotal);
    }

    [Fact]
    public void OpenEndedRecordKeepsContributingIndefinitely()
    {
        var records = new[] { Record(412.55m, new DateOnly(2024, 1, 1), null) };

        Assert.Equal(412.55m, CompensationOtherIncome.AmountsOn(records, new DateOnly(2024, 1, 1)).MonthlyTotal);
        Assert.Equal(4_950.60m, CompensationOtherIncome.AmountsOn(records, new DateOnly(2099, 12, 31)).AnnualTotal);
    }

    [Fact]
    public void UnflaggedRecordShowsInTheTrackButNotInThePersonallyAvailableTotal()
    {
        var records = new[]
        {
            Record(300m, new DateOnly(2024, 1, 1), null, countsTowardPersonalIncome: true),
            Record(500m, new DateOnly(2024, 1, 1), null, countsTowardPersonalIncome: false)
        };

        var amounts = CompensationOtherIncome.AmountsOn(records, new DateOnly(2025, 1, 1));

        Assert.Equal(800m, amounts.MonthlyTotal);
        Assert.Equal(9_600m, amounts.AnnualTotal);
        Assert.Equal(300m, amounts.MonthlyCounted);
        Assert.Equal(3_600m, amounts.AnnualCounted);
    }

    [Fact]
    public void RecordsOfSeveralMembersSumOnTheSameDate()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var records = new[]
        {
            Record(300m, new DateOnly(2024, 1, 1), null, userId: alice),
            Record(250m, new DateOnly(2024, 1, 1), null, userId: bob),
            // Bob's second record has already expired on the queried date.
            Record(999m, new DateOnly(2020, 1, 1), new DateOnly(2021, 1, 1), userId: bob)
        };

        var amounts = CompensationOtherIncome.AmountsOn(records, new DateOnly(2025, 1, 1));

        Assert.Equal(550m, amounts.MonthlyTotal);
        Assert.Equal(6_600m, amounts.AnnualTotal);
    }

    [Fact]
    public void EmptyTrackIsZeroAndNeverNegative()
    {
        var amounts = CompensationOtherIncome.AmountsOn([], new DateOnly(2025, 1, 1));

        Assert.Equal(CompensationOtherIncomeAmounts.Zero, amounts);
    }

    [Theory]
    [InlineData("Halbwaisenrente", "halbwaisenrente")]
    [InlineData("  Private Rente ", "private-rente")]
    [InlineData("Rente/Pension", "rente-pension")]
    [InlineData("Etwas   ganz  Neues", "etwas-ganz-neues")]
    public void TypeIsAnOpenNormalizedSetRatherThanAFixedEnum(string input, string expected) =>
        Assert.Equal(expected, CompensationOtherIncome.NormalizeType(input));

    [Fact]
    public void SuggestedTypesAreOnlySuggestionsAndIncludeHalbwaisenrente()
    {
        Assert.Contains(CompensationOtherIncome.SuggestedTypes, x => x.Type == "halbwaisenrente");

        // An unlisted type still validates — the set is open.
        CompensationOtherIncome.Validate(Write("Erbbaurechtszins"));
    }

    [Fact]
    public void ValidationRejectsBrokenRecords()
    {
        Assert.Throws<ArgumentException>(() => CompensationOtherIncome.Validate(Write("   ")));
        Assert.Throws<ArgumentException>(() => CompensationOtherIncome.Validate(Write("rente", amount: -1m)));
        Assert.Throws<ArgumentException>(() => CompensationOtherIncome.Validate(
            Write("rente", validFrom: new DateOnly(2025, 1, 1), validTo: new DateOnly(2024, 12, 31))));
        Assert.Throws<ArgumentException>(() => CompensationOtherIncome.Validate(
            Write("rente", note: new string('x', CompensationOtherIncome.MaxNoteLength + 1))));
    }

    [Fact]
    public void ValidationAcceptsAnOpenEndedRecord() =>
        CompensationOtherIncome.Validate(Write("halbwaisenrente", validTo: null));

    private static CompensationOtherIncomeWrite Write(
        string type,
        decimal amount = 300m,
        DateOnly? validFrom = null,
        DateOnly? validTo = null,
        string? note = null) => new(
        type,
        null,
        amount,
        validFrom ?? new DateOnly(2024, 1, 1),
        validTo,
        true,
        note);

    private static CompensationOtherIncomeEntry Record(
        decimal monthly,
        DateOnly validFrom,
        DateOnly? validTo,
        bool countsTowardPersonalIncome = true,
        Guid? userId = null) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        userId ?? Guid.NewGuid(),
        "halbwaisenrente",
        "Halbwaisenrente",
        monthly,
        monthly * 12m,
        validFrom,
        validTo,
        countsTowardPersonalIncome,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}
