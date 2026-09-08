namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Write model for a regularly recurring income that is neither salary nor an employer benefit —
/// a Halbwaisenrente is the canonical case. <see cref="Type"/> is an OPEN set (a normalized slug),
/// not an enum, so new kinds of income never need a code change.
/// </summary>
public sealed record CompensationOtherIncomeWrite(
    string Type,
    string? Label,
    decimal MonthlyAmount,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    bool CountsTowardPersonalIncome,
    string? Note);

/// <summary>
/// A stored other-income record. It never enters the salary calculation: the employer total package and
/// every other <see cref="GermanCompensationCalculator"/> figure are computed without knowing this exists.
/// </summary>
public sealed record CompensationOtherIncomeEntry(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid UserId,
    string Type,
    string? Label,
    decimal MonthlyAmount,
    decimal AnnualAmount,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    bool CountsTowardPersonalIncome,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A suggested (not enforced) type for the type picker.</summary>
public sealed record CompensationOtherIncomeTypeOption(string Type, string Label);

/// <summary>
/// Other-income sums valid on one specific date. "Counted" only contains records whose
/// <see cref="CompensationOtherIncomeEntry.CountsTowardPersonalIncome"/> flag is set.
/// </summary>
public readonly record struct CompensationOtherIncomeAmounts(
    decimal MonthlyTotal,
    decimal MonthlyCounted,
    decimal AnnualTotal,
    decimal AnnualCounted)
{
    public static CompensationOtherIncomeAmounts Zero => default;
}
