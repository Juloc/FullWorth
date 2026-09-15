using FullWorth.Backend.Modules.Budgets.Suggestions;
using Xunit;

namespace FullWorth.Backend.Tests.Budgets;

/// <summary>
/// Der Budget-Assistent schlaegt aus den Buchungen vor, nicht aus einer Tabelle von Kategorieregeln
/// (#115). Diese Tests halten genau das fest: dieselbe Kategorie mit anderem Rhythmus ergibt einen
/// anderen Vorschlag, und ohne Historie gibt es trotzdem einen - nur mit niedriger Sicherheit.
/// </summary>
public sealed class BudgetSuggestionTests
{
    private static readonly DateOnly Today = new(2026, 9, 15);

    private static List<BudgetHistoryEntry> Every(int days, int count, decimal amount, DateOnly? until = null)
    {
        var end = until ?? Today;
        var entries = new List<BudgetHistoryEntry>();
        for (var index = 0; index < count; index++)
            entries.Add(new BudgetHistoryEntry(end.AddDays(-days * index), -amount));
        entries.Reverse();
        return entries;
    }

    [Fact]
    public void WeeklySpendingSuggestsWeekly()
    {
        // Jede Woche zwei Einkaeufe, seit einem halben Jahr.
        var history = Every(7, 26, 45m).Concat(Every(7, 26, 38m, Today.AddDays(-3))).ToList();

        var best = BudgetCadenceInference.Best(history, Today);

        Assert.Equal(BudgetCadence.Weekly, best.Cadence);
        Assert.True(best.Confidence > BudgetCadenceInference.WeakConfidence,
            $"Sicherheit war {best.Confidence}");
    }

    [Fact]
    public void MonthlySpendingSuggestsMonthly()
    {
        // Einmal im Monat, zwoelf Monate lang - woechentlich waere dasselbe Geld in drei von vier
        // Wochen gar nichts, und genau daran faellt der kuerzere Kandidat durch.
        var history = Every(30, 12, 240m);

        var best = BudgetCadenceInference.Best(history, Today);

        Assert.Equal(BudgetCadence.Monthly, best.Cadence);
    }

    [Fact]
    public void YearlySpendingCanBeRecognisedWithEnoughHistory()
    {
        var history = new List<BudgetHistoryEntry>
        {
            new(new DateOnly(2023, 11, 4), -890m),
            new(new DateOnly(2024, 11, 6), -910m),
            new(new DateOnly(2025, 11, 3), -880m)
        };

        var ranked = BudgetCadenceInference.Rank(history, Today);

        // Jaehrlich muss es zumindest anfuehren; monatlich waere elf von zwoelf Perioden leer.
        Assert.Equal(BudgetCadence.Yearly, ranked[0].Cadence);
    }

    [Fact]
    public void NoHistoryStillProducesAnEditableMonthlyFallback()
    {
        var best = BudgetCadenceInference.Best([], Today);

        Assert.Equal(BudgetCadence.Monthly, best.Cadence);
        Assert.Equal(0m, best.Confidence);
        Assert.True(best.Confidence < BudgetCadenceInference.WeakConfidence);
    }

    [Fact]
    public void TheCurrentPeriodIsNeverCountedAsComplete()
    {
        var periods = BudgetCadenceInference.CompletePeriods(
            BudgetCadence.Monthly, new DateOnly(2026, 6, 10), Today);

        // Juni, Juli, August - der laufende September fehlt.
        Assert.Equal(3, periods.Count);
        Assert.Equal(new DateOnly(2026, 8, 31), periods[^1].End);
    }

    [Fact]
    public void AmountProposalRoundsUpAndWeightsRecentPeriodsHigher()
    {
        // Die juengsten Wochen liegen hoeher; der Vorschlag muss ueber dem schlichten Mittel liegen.
        var sums = new List<decimal> { 70m, 75m, 80m, 95m, 100m, 110m };
        var plain = sums.Sum() / sums.Count;                       // 88,33

        var proposal = BudgetAmountSuggestion.Propose(sums, BudgetCadence.Weekly);

        Assert.True(proposal.Average > plain, $"Durchschnitt war {proposal.Average}");
        Assert.True(proposal.Suggested >= proposal.Average);
        Assert.Equal(0m, proposal.Suggested % 5m);                 // 95, nicht 92,18
        Assert.Equal(6, proposal.PeriodsUsed);
    }

    [Fact]
    public void ASingleOutlierDoesNotBlowUpTheProposal()
    {
        var steady = new List<decimal> { 90m, 95m, 92m, 88m, 94m, 91m };
        var withSpike = new List<decimal> { 90m, 95m, 92m, 88m, 94m, 2000m };

        var a = BudgetAmountSuggestion.Propose(steady, BudgetCadence.Weekly);
        var b = BudgetAmountSuggestion.Propose(withSpike, BudgetCadence.Weekly);

        Assert.Equal(1, b.OutliersDamped);
        // Ohne Daempfung laege der gewichtete Durchschnitt ueber 600. Gedaempft bleibt er im Rahmen -
        // das Geld verschwindet nicht, aber es bestimmt nicht das Budget.
        Assert.True(b.Average < a.Average * 3m, $"Vorschlag war {b.Average} gegen {a.Average}");
        Assert.True(b.Average > a.Average, "der Ausreisser darf auch nicht spurlos verschwinden");
    }

    [Fact]
    public void RoundingProducesNumbersAPersonWouldWrite()
    {
        Assert.Equal(95m, BudgetAmountSuggestion.RoundUpNicely(92.18m));
        Assert.Equal(12m, BudgetAmountSuggestion.RoundUpNicely(11.4m));
        Assert.Equal(250m, BudgetAmountSuggestion.RoundUpNicely(241m));
        Assert.Equal(1050m, BudgetAmountSuggestion.RoundUpNicely(1043m));
        Assert.Equal(0m, BudgetAmountSuggestion.RoundUpNicely(0m));
    }

    [Fact]
    public void IncomePeriodsStartAtTheRealBookingAndMoveWithIt()
    {
        // Das Gehalt kommt am 27., faellt der auf ein Wochenende, kommt es frueher. Die Periodengrenze
        // verschiebt sich mit - genau das kann ein fester Stichtag nicht.
        var dates = new List<DateOnly>
        {
            new(2026, 1, 27), new(2026, 2, 27), new(2026, 3, 26)
        };

        var periods = BudgetIncomePeriods.Build(dates, new DateOnly(2026, 4, 10));

        Assert.Equal(3, periods.Count);
        Assert.Equal(new DateOnly(2026, 1, 27), periods[0].Start);
        Assert.Equal(new DateOnly(2026, 2, 26), periods[0].End);
        Assert.Equal(new DateOnly(2026, 2, 27), periods[1].Start);
        Assert.Equal(new DateOnly(2026, 3, 25), periods[1].End);
        Assert.Equal(new DateOnly(2026, 3, 26), periods[2].Start);
    }

    [Fact]
    public void AMissingIncomeBookingFallsBackToTheExpectedDateInsteadOfLeavingThePeriodOpen()
    {
        var dates = new List<DateOnly> { new(2026, 1, 27), new(2026, 2, 27), new(2026, 3, 27) };

        // Der April fehlt. Der typische Abstand ist rund 30 Tage, also wird der 26.4. erwartet.
        var expected = BudgetIncomePeriods.ExpectedNext(dates);
        var periods = BudgetIncomePeriods.Build(dates, new DateOnly(2026, 4, 20));

        Assert.NotNull(expected);
        Assert.True(expected!.Value > new DateOnly(2026, 4, 20), $"erwartet war {expected}");
        Assert.Equal(expected.Value.AddDays(-1), periods[^1].End);
    }

    [Fact]
    public void AnExpectedDateThatPassedWithoutABookingKeepsThePeriodRunningUntilToday()
    {
        var dates = new List<DateOnly> { new(2026, 1, 27), new(2026, 2, 27) };
        var today = new DateOnly(2026, 5, 10);   // laengst ueberfaellig

        var periods = BudgetIncomePeriods.Build(dates, today);

        Assert.Equal(today, periods[^1].End);
    }
}
