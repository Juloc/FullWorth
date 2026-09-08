using System.Globalization;
using System.Text;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Pure logic for the "sonstige regelmäßige Einkünfte" track: normalization, validation and the
/// date-window aggregation used by the compensation timeline.
///
/// This class deliberately knows nothing about <see cref="GermanCompensationCalculator"/>. Other income
/// is an ADDITIVE track next to the salary figures — it is never fed into the salary calculation, so the
/// employer total package (and every other calculator output) is bit-for-bit unaffected by its presence.
/// </summary>
public static class CompensationOtherIncome
{
    public const decimal MaxMonthlyAmount = 1_000_000m;
    public const int MaxTypeLength = 60;
    public const int MaxLabelLength = 120;
    public const int MaxNoteLength = 1000;

    /// <summary>
    /// Suggestions for the type picker only. The stored type is an open set — anything that normalizes to
    /// a non-empty slug is accepted, so an unforeseen kind of income needs no backend change.
    /// </summary>
    public static readonly IReadOnlyList<CompensationOtherIncomeTypeOption> SuggestedTypes =
    [
        new("halbwaisenrente", "Halbwaisenrente"),
        new("waisenrente", "Waisenrente"),
        new("witwenrente", "Witwen-/Witwerrente"),
        new("erwerbsminderungsrente", "Erwerbsminderungsrente"),
        new("unfallrente", "Unfallrente"),
        new("betriebsrente", "Betriebsrente"),
        new("private-rente", "Private Rente"),
        new("unterhalt", "Unterhalt"),
        new("kindergeld", "Kindergeld"),
        new("elterngeld", "Elterngeld"),
        new("bafoeg", "BAföG"),
        new("stipendium", "Stipendium"),
        new("mieteinnahmen", "Mieteinnahmen"),
        new("nebentaetigkeit", "Nebentätigkeit"),
        new("sonstiges", "Sonstiges")
    ];

    /// <summary>
    /// Folds a free-text type into a stable lowercase slug ("Private Rente" → "private-rente") so the same
    /// kind of income groups together regardless of how it was typed.
    /// </summary>
    public static string NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return string.Empty;

        var builder = new StringBuilder(type.Length);
        foreach (var character in type.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
            else if (character is ' ' or '-' or '_' or '/' or '.' or '\t')
                builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length > MaxTypeLength ? slug[..MaxTypeLength].Trim('-') : slug;
    }

    public static string? CleanText(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) throw new ArgumentException($"{fieldName} is too long.");
        return trimmed;
    }

    public static void Validate(CompensationOtherIncomeWrite write)
    {
        if (NormalizeType(write.Type).Length == 0)
            throw new ArgumentException("Other-income type is required.");
        if (write.MonthlyAmount < 0m)
            throw new ArgumentException("Other-income amount cannot be negative.");
        if (write.MonthlyAmount > MaxMonthlyAmount)
            throw new ArgumentException("Other-income amount is implausibly high.");
        if (write.ValidFrom.Year is < 1900 or > 2200)
            throw new ArgumentOutOfRangeException(nameof(write.ValidFrom));
        if (write.ValidTo is { } to)
        {
            if (to.Year is < 1900 or > 2200)
                throw new ArgumentOutOfRangeException(nameof(write.ValidTo));
            if (to < write.ValidFrom)
                throw new ArgumentException("Other-income end date cannot be before the start date.");
        }
        _ = CleanText(write.Label, MaxLabelLength, "Other-income label");
        _ = CleanText(write.Note, MaxNoteLength, "Other-income note");
    }

    /// <summary>Inclusive on both ends; a null <c>ValidTo</c> means open-ended and never stops contributing.</summary>
    public static bool IsActiveOn(CompensationOtherIncomeEntry entry, DateOnly date) =>
        entry.ValidFrom <= date && (entry.ValidTo is null || entry.ValidTo.Value >= date);

    /// <summary>
    /// Sums every record whose von/bis window contains <paramref name="date"/>. The "counted" half only
    /// adds records flagged as part of the personally available income.
    /// </summary>
    public static CompensationOtherIncomeAmounts AmountsOn(
        IEnumerable<CompensationOtherIncomeEntry> entries, DateOnly date)
    {
        var monthlyTotal = 0m;
        var monthlyCounted = 0m;
        foreach (var entry in entries)
        {
            if (!IsActiveOn(entry, date)) continue;
            monthlyTotal += entry.MonthlyAmount;
            if (entry.CountsTowardPersonalIncome) monthlyCounted += entry.MonthlyAmount;
        }

        return new CompensationOtherIncomeAmounts(
            RoundMoney(monthlyTotal),
            RoundMoney(monthlyCounted),
            RoundMoney(monthlyTotal * 12m),
            RoundMoney(monthlyCounted * 12m));
    }

    public static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    internal static DateOnly DateValue(object? value) => value switch
    {
        DateOnly date => date,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        _ => DateOnly.Parse(
            value?.ToString() ?? throw new InvalidOperationException("Missing other-income date."),
            CultureInfo.InvariantCulture)
    };
}
