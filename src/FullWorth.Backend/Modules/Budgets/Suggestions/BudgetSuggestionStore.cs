using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Budgets.CarryOver;
using FullWorth.Backend.Modules.Budgets.Cycles;
using FullWorth.Backend.Modules.Reconciliation;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Budgets.Suggestions;

/// <summary>Was der Wizard wissen will: worauf das Budget zaehlen soll.</summary>
/// <param name="Categories">Ganze Kategorien oder einzelne Unterkategorien, beliebig gemischt.</param>
/// <param name="IncomeCategoryId">Optional: die Kategorie der Einnahme, an die die Periode gekoppelt wird.</param>
public sealed record BudgetSuggestionRequest(
    IReadOnlyList<CategoryScopeWrite>? Categories,
    Guid? IncomeCategoryId);

/// <summary>Ein Intervallvorschlag, wie ihn die Oberflaeche zeigt.</summary>
public sealed record BudgetCadenceOption(
    string Cadence, decimal Confidence, int CompletePeriods, decimal ActiveShare, decimal Variation);

/// <summary>Die Antwort auf "was schlaegst du vor?".</summary>
/// <param name="Cadence">Das empfohlene Intervall.</param>
/// <param name="Options">Alle vier Intervalle mit ihrer Bewertung - der Benutzer darf jedes waehlen.</param>
/// <param name="Confidence">Die Sicherheit des empfohlenen Intervalls, 0 bis 1.</param>
/// <param name="WeakData">Wahr, wenn die Datenlage duenn ist. Die Oberflaeche sagt es dann.</param>
/// <param name="Average">Der historische Durchschnitt je Periode.</param>
/// <param name="Suggested">Der gerundete Betragsvorschlag.</param>
/// <param name="PeriodsUsed">Wie viele abgeschlossene Perioden eingeflossen sind.</param>
/// <param name="OutliersDamped">Wie viele davon als Ausreisser gedaempft wurden.</param>
/// <param name="Currency">Die Basiswaehrung des Bereichs.</param>
/// <param name="MatchingTransactions">Wie viele Buchungen ueberhaupt in den Bereich fielen.</param>
/// <param name="IncomeDates">Die erkannten Einnahmetermine, falls gekoppelt werden soll.</param>
/// <param name="ExpectedNextIncome">Der erwartete naechste Einnahmetermin - der Rueckfall, wenn er ausbleibt.</param>
public sealed record BudgetSuggestionView(
    string Cadence,
    IReadOnlyList<BudgetCadenceOption> Options,
    decimal Confidence,
    bool WeakData,
    decimal Average,
    decimal Suggested,
    int PeriodsUsed,
    int OutliersDamped,
    string Currency,
    int MatchingTransactions,
    IReadOnlyList<DateOnly> IncomeDates,
    DateOnly? ExpectedNextIncome);

/// <summary>Eine rekonstruierte Periode in der Vorschau.</summary>
public sealed record BudgetPreviewPeriod(
    DateOnly Start, DateOnly End, decimal Spent, decimal CarriedIn, decimal Available, decimal Remaining);

/// <summary>Die Vorschau auf eine noch nicht gespeicherte Wizard-Konfiguration.</summary>
public sealed record BudgetPreviewView(
    IReadOnlyList<BudgetPreviewPeriod> Periods, decimal CarryInToday, string Currency);

/// <summary>Wovon an der Uebertrag gerechnet wird.</summary>
public enum CarryOverStart { AsFarBackAsPossible, ThisPeriod, FromDate }

/// <summary>Die vollstaendige Wizard-Konfiguration, wie sie die Vorschau braucht.</summary>
public sealed record BudgetPreviewRequest(
    IReadOnlyList<CategoryScopeWrite>? Categories,
    string Cadence,
    decimal Amount,
    bool CarryOver,
    string CarryOverStart,
    DateOnly? CarryOverFrom,
    Guid? IncomeCategoryId,
    int? WeekStartDay,
    int? MonthStartDay);

/// <summary>
/// Der Unterbau des Budget-Assistenten (#115): was die Buchungen ueber Rhythmus und Hoehe sagen, und
/// wie eine noch nicht gespeicherte Konfiguration ausgesehen haette.
///
/// Beide Wege schreiben NICHTS. Ein Vorschlag, der nebenbei etwas anlegt, waere genau die Art von
/// stiller Aenderung, die das Issue verbietet - gespeichert wird erst nach der Bestaetigung.
///
/// Gerechnet wird mit den vorhandenen Bausteinen: <see cref="BudgetCycleCalculator"/> fuer die
/// Kalenderperioden, <see cref="BudgetIncomePeriods"/> fuer die an eine Einnahme gekoppelten, und
/// <see cref="BudgetCarryOverCalculator"/> fuer den Uebertrag. Keine zweite Rechnung daneben.
/// </summary>
public sealed class BudgetSuggestionStore(FullWorthDbContext db, BudgetScopeStore scopes)
{
    /// <summary>So weit zurueck wird hoechstens gesucht - drei Jahre reichen auch fuer ein Jahresbudget.</summary>
    private const int MaxHistoryDays = 1100;

    public async Task<BudgetSuggestionView> SuggestAsync(
        Guid userId, Guid fullWorthSpaceId, BudgetSuggestionRequest request, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currency = await BaseCurrencyAsync(fullWorthSpaceId, ct);
        var history = await HistoryAsync(userId, fullWorthSpaceId, request.Categories, today, ct);

        var ranked = BudgetCadenceInference.Rank(history, today);
        var best = ranked[0];
        var periodSums = PeriodSums(history, best.Cadence, today);
        var amount = BudgetAmountSuggestion.Propose(periodSums, best.Cadence);

        var incomeDates = request.IncomeCategoryId.HasValue
            ? (await HistoryAsync(userId, fullWorthSpaceId,
                    [new CategoryScopeWrite(request.IncomeCategoryId.Value, true)], today, ct))
                .Where(entry => entry.Amount > 0m)
                .Select(entry => entry.Date)
                .Distinct().OrderBy(date => date).ToList()
            : [];

        return new BudgetSuggestionView(
            best.Cadence.ToString(),
            [.. ranked.Select(option => new BudgetCadenceOption(
                option.Cadence.ToString(), option.Confidence, option.CompletePeriods, option.ActiveShare, option.Variation))],
            best.Confidence,
            best.Confidence < BudgetCadenceInference.WeakConfidence,
            amount.Average,
            amount.Suggested,
            amount.PeriodsUsed,
            amount.OutliersDamped,
            currency,
            history.Count,
            incomeDates,
            BudgetIncomePeriods.ExpectedNext(incomeDates));
    }

    public async Task<BudgetPreviewView> PreviewAsync(
        Guid userId, Guid fullWorthSpaceId, BudgetPreviewRequest request, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var currency = await BaseCurrencyAsync(fullWorthSpaceId, ct);
        var history = await HistoryAsync(userId, fullWorthSpaceId, request.Categories, today, ct);
        if (history.Count == 0) return new BudgetPreviewView([], 0m, currency);

        var periods = request.IncomeCategoryId.HasValue
            ? await IncomePeriodsAsync(userId, fullWorthSpaceId, request.IncomeCategoryId.Value, today, ct)
            : CalendarPeriods(request, history[0].Date, today);
        if (periods.Count == 0) return new BudgetPreviewView([], 0m, currency);

        // Ab wann der Uebertrag zaehlt, ist eine ANDERE Frage als der Periodenbeginn (#115). Die Perioden
        // stehen bereits; hier wird nur entschieden, ab welcher davon gerechnet wird.
        var start = Enum.TryParse<CarryOverStart>(request.CarryOverStart, ignoreCase: true, out var parsed)
            ? parsed : CarryOverStart.AsFarBackAsPossible;
        var from = start switch
        {
            CarryOverStart.ThisPeriod => periods[^1].Start,
            CarryOverStart.FromDate => request.CarryOverFrom ?? periods[0].Start,
            _ => periods[0].Start
        };
        var counted = periods.Where(period => period.End >= from).ToList();
        if (counted.Count == 0) counted = [periods[^1]];

        var mode = request.CarryOver ? CarryOverMode.Enabled : CarryOverMode.Disabled;
        var rows = new List<BudgetPreviewPeriod>();
        var spends = new List<decimal>();
        foreach (var period in counted)
        {
            var spent = history
                .Where(entry => entry.Date >= period.Start && entry.Date <= period.End)
                .Sum(entry => Math.Abs(entry.Amount));
            var carriedIn = BudgetCarryOverCalculator.CarriedIn(mode, request.Amount, spends);
            var available = request.Amount + carriedIn;
            rows.Add(new BudgetPreviewPeriod(
                period.Start, period.End, spent, carriedIn, available, available - spent));
            spends.Add(spent);
        }

        return new BudgetPreviewView(rows, BudgetCarryOverCalculator.CarriedIn(mode, request.Amount, spends), currency);
    }

    // ---- innen ----

    private async Task<string> BaseCurrencyAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId)
            .Select(space => space.BaseCurrency)
            .SingleOrDefaultAsync(ct) ?? "EUR";

    /// <summary>Die Buchungen des Bereichs, aelteste zuerst. Ohne Kategorieauswahl ist die Liste leer.</summary>
    private async Task<List<BudgetHistoryEntry>> HistoryAsync(
        Guid userId, Guid fullWorthSpaceId, IReadOnlyList<CategoryScopeWrite>? categories,
        DateOnly today, CancellationToken ct)
    {
        if (categories is null || categories.Count == 0) return [];

        var visible = await RawSql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        if (visible.Count == 0) return [];

        var wanted = await scopes.ExpandCategoryScopesAsync(fullWorthSpaceId, categories, ct);
        if (wanted.Count == 0) return [];

        var transactions = await scopes.TransactionsInWindowAsync(
            visible, today.AddDays(-MaxHistoryDays), today, ct);

        return [.. transactions
            .Where(transaction => transaction.CategoryId.HasValue && wanted.Contains(transaction.CategoryId.Value))
            .Select(transaction => new BudgetHistoryEntry(
                transaction.BookingDate ?? transaction.ValueDate!.Value, transaction.Amount))
            .OrderBy(entry => entry.Date)];
    }

    private static List<decimal> PeriodSums(
        IReadOnlyList<BudgetHistoryEntry> history, BudgetCadence cadence, DateOnly today)
    {
        if (history.Count == 0) return [];
        return [.. BudgetCadenceInference.CompletePeriods(cadence, history[0].Date, today)
            .Select(period => history
                .Where(entry => entry.Date >= period.Start && entry.Date <= period.End)
                .Sum(entry => Math.Abs(entry.Amount)))];
    }

    /// <summary>Die Kalenderperioden zur gewaehlten Einstellung - inklusive der laufenden.</summary>
    private static List<BudgetCyclePeriod> CalendarPeriods(
        BudgetPreviewRequest request, DateOnly first, DateOnly today)
    {
        var cadence = Enum.TryParse<BudgetCadence>(request.Cadence, ignoreCase: true, out var parsed)
            ? parsed : BudgetCadence.Monthly;

        var definition = cadence switch
        {
            // Der Wochenstart ist frei waehlbar: der Anker wird auf den gewuenschten Wochentag gelegt.
            BudgetCadence.Weekly => BudgetCycleDefinition.Custom(AlignToWeekday(first, request.WeekStartDay), 7),
            // Ein eigener Monatsstichtag ist der PayCycle, den es schon gibt - kein zweiter Zyklustyp.
            BudgetCadence.Monthly when request.MonthStartDay is > 1 and <= 31 =>
                BudgetCycleDefinition.PayCycle(request.MonthStartDay.Value),
            _ => BudgetCadenceInference.Definition(cadence, first)
        };

        var periods = new List<BudgetCyclePeriod>();
        var cursor = BudgetCycleCalculator.CurrentPeriod(definition, first);
        var current = BudgetCycleCalculator.CurrentPeriod(definition, today);
        for (var guard = 0; cursor.Start <= current.Start && guard < 600; guard++)
        {
            periods.Add(cursor);
            cursor = BudgetCycleCalculator.NextPeriod(definition, cursor.Start);
        }
        return periods;
    }

    /// <summary>Den Anker auf den gewuenschten Wochentag zuruecksetzen (1 = Montag … 7 = Sonntag).</summary>
    private static DateOnly AlignToWeekday(DateOnly date, int? weekStartDay)
    {
        if (weekStartDay is not (>= 1 and <= 7)) return date;
        var wanted = weekStartDay.Value % 7;                       // 7 (Sonntag) -> 0, passend zu DayOfWeek
        var shift = ((int)date.DayOfWeek - wanted + 7) % 7;
        return date.AddDays(-shift);
    }

    private async Task<List<BudgetCyclePeriod>> IncomePeriodsAsync(
        Guid userId, Guid fullWorthSpaceId, Guid incomeCategoryId, DateOnly today, CancellationToken ct)
    {
        var income = await HistoryAsync(
            userId, fullWorthSpaceId, [new CategoryScopeWrite(incomeCategoryId, true)], today, ct);
        var dates = income.Where(entry => entry.Amount > 0m).Select(entry => entry.Date).ToList();
        return [.. BudgetIncomePeriods.Build(dates, today)];
    }
}
