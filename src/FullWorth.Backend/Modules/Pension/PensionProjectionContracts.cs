namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// Step 3 of docs/PENSION.md, the projection half: what a contract might be worth at retirement, and
/// how two of them compare.
///
/// The whole feature exists under one constraint, and it is the reason these types look the way they do:
/// <b>a projection must never be able to pass for a value.</b> Step 1 enforced that in the database —
/// only <c>BavSnapshot.Balance</c> reaches the asset, and a projected figure cannot be stored without
/// its basis and its return assumption. This layer computes, and it computes <b>on read only</b>: no
/// method here writes a row, so a scenario the user tried cannot leak into net worth later.
///
/// The second constraint is the one that makes a comparison honest. With the same return and the same
/// costs, <c>50 € + 288 €</c> is <c>338 €</c>: splitting a contribution across two contracts produces no
/// extra compound interest, and any comparison that shows an advantage there is lying. So
/// <see cref="BavComparisonResult"/> cannot report a difference without naming which of the three real
/// causes produced it — see <see cref="BavDeltaCause"/>.
/// </summary>
public sealed record BavProjectionRequest(
    /// <summary>The contracts to project. Empty means every contract that still holds capital.</summary>
    IReadOnlyList<Guid>? ContractIds = null,
    /// <summary>
    /// Annual return in percent. The UI offers 3 / 5 / 7 and a free field; there is no default here,
    /// because a projection without a stated assumption is exactly what this feature must not produce.
    /// </summary>
    decimal ReturnPercent = 5m,
    /// <summary>
    /// Retirement date to project to. Null uses each contract's own <c>RetirementDate</c>; a contract
    /// without one cannot be projected and says so rather than borrowing somebody else's date.
    /// </summary>
    DateOnly? RetirementDate = null,
    /// <summary>
    /// Whether to keep paying the current contribution until retirement. False projects the balance
    /// alone, which is the honest reading for a <c>paid_up</c> contract.
    /// </summary>
    bool ContinueContributions = true);

/// <summary>
/// One contract's projection. Everything here is an assumption except <see cref="CurrentBalance"/> and
/// <see cref="GuaranteedCapital"/> — and those two are the only figures the user has been promised.
/// </summary>
public sealed record BavContractProjection(
    Guid ContractId,
    string ProviderName,
    string Currency,
    /// <summary>Today's balance, from the newest snapshot. A fact, not a projection.</summary>
    decimal? CurrentBalance,
    DateOnly? BalanceAsOf,
    /// <summary>What the document guaranteed, where it guaranteed anything. A fact about the contract.</summary>
    decimal? GuaranteedCapital,
    decimal? GuaranteedMonthlyAnnuity,
    /// <summary>FullWorth's own arithmetic. Always labelled, never stored.</summary>
    decimal? ProjectedCapital,
    /// <summary>
    /// Derived from <see cref="ProjectedCapital"/> and the contract's guaranteed annuity factor. Null
    /// when the contract states no factor — a monthly annuity invented from a capital sum would be the
    /// most misleading number on the screen.
    /// </summary>
    decimal? ProjectedMonthlyAnnuity,
    decimal ReturnPercent,
    int MonthsToRetirement,
    /// <summary>The employee share paid in from today until retirement. What this costs the user.</summary>
    decimal EmployeeContributionsAhead,
    /// <summary>The employer share over the same period. A benefit — never a cost, never spendable.</summary>
    decimal EmployerContributionsAhead,
    /// <summary>
    /// The known costs applied to the projection, and only the known ones. A cost marked estimated is
    /// applied and reported as estimated; a cost the documents never stated is not invented.
    /// </summary>
    decimal CostsApplied,
    bool CostsIncludeEstimates,
    /// <summary>Why this contract could not be projected, when it could not. See <see cref="BavProjectionBlockers"/>.</summary>
    string? Blocker = null);

/// <summary>Why a contract cannot be projected. Each one is a missing fact, never a guess FullWorth makes.</summary>
public static class BavProjectionBlockers
{
    /// <summary>No retirement date on the contract and none supplied by the caller.</summary>
    public const string NoRetirementDate = "no_retirement_date";

    /// <summary>No snapshot at all, so there is no balance to grow.</summary>
    public const string NoBalance = "no_balance";

    /// <summary>The retirement date is in the past: this contract is in or past payout.</summary>
    public const string AlreadyDue = "already_due";

    /// <summary>A currency in the set has no rate, so a total would be short a whole contract.</summary>
    public const string MissingRate = "missing_rate";
}

/// <summary>
/// The projection over a set of contracts. Sums are per base currency and follow the same rule as every
/// other total in this product: a missing rate makes the result incomplete rather than silently short.
/// </summary>
public sealed record BavProjectionResult(
    string Currency,
    decimal ReturnPercent,
    IReadOnlyList<BavContractProjection> Contracts,
    decimal TotalCurrentBalance,
    decimal TotalGuaranteedCapital,
    decimal TotalProjectedCapital,
    decimal TotalGuaranteedMonthlyAnnuity,
    decimal TotalProjectedMonthlyAnnuity,
    bool IsComplete,
    IReadOnlyList<string> MissingCurrencies,
    /// <summary>Contracts left out of the totals, with the reason. Silence about them would be the bug.</summary>
    IReadOnlyList<BavContractProjection> Excluded);

/// <summary>
/// A comparison of two projections of the same money. <see cref="Cause"/> is not optional decoration:
/// it is what stops the comparison from claiming an advantage that arithmetic cannot produce.
/// </summary>
public sealed record BavComparisonResult(
    BavProjectionResult Left,
    BavProjectionResult Right,
    decimal CapitalDelta,
    decimal AnnuityDelta,
    /// <summary>
    /// Which real difference produced the delta, or <see cref="BavDeltaCause.None"/> when the two are
    /// the same money under the same assumptions and the delta is therefore zero.
    /// </summary>
    IReadOnlyList<string> Cause,
    /// <summary>
    /// True when the two sides carry the same total contribution at the same return and the same costs.
    /// Then the capital delta MUST be zero, and the UI says so in words: splitting a contribution
    /// across contracts produces no extra compound interest.
    /// </summary>
    bool SameMoneySameAssumptions);

/// <summary>
/// The only three things that can make two bAV contracts differ for the same money. Anything else in a
/// comparison is an artefact, which is why the result has to name one of these.
/// </summary>
public static class BavDeltaCause
{
    public const string Costs = "costs";
    public const string Guarantee = "guarantee";
    public const string InvestmentConcept = "investment_concept";

    /// <summary>Same money, same assumptions: the delta is zero and there is nothing to attribute.</summary>
    public const string None = "none";

    /// <summary>The two sides assume different returns, so they are not comparable as contracts at all.</summary>
    public const string DifferentAssumptions = "different_assumptions";
}

/// <summary>
/// The projection arithmetic. Pure and side-effect free on purpose: it takes facts in and returns
/// numbers, so it can be unit-tested to the cent without a database, and so nothing it produces can
/// accidentally be persisted.
/// </summary>
public interface IBavProjectionCalculator
{
    /// <summary>
    /// Grows <paramref name="balance"/> for <paramref name="months"/> at <paramref name="annualPercent"/>,
    /// adding <paramref name="monthlyContribution"/> at the start of each month and applying
    /// <paramref name="annualCostPercent"/> of capital per year.
    ///
    /// Monthly compounding from an annual rate uses the twelfth root, not <c>annual / 12</c>: the latter
    /// overstates a 7 % assumption by about 0.23 percentage points a year, which over thirty years is
    /// real money in the user's favour on screen and not in their account.
    /// </summary>
    decimal Project(
        decimal balance,
        decimal monthlyContribution,
        int months,
        decimal annualPercent,
        decimal annualCostPercent);
}
