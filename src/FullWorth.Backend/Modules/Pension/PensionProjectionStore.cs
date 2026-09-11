using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Pension;

/// <summary>A projection read. <see cref="BavMutationResult.NotFound"/> is what a non-member gets, so existence does not leak.</summary>
public sealed record BavProjectionOutcome(
    BavMutationResult Result,
    BavProjectionResult? Projection = null,
    string? Error = null);

/// <summary>A comparison read, with the same status semantics as <see cref="BavProjectionOutcome"/>.</summary>
public sealed record BavComparisonOutcome(
    BavMutationResult Result,
    BavComparisonResult? Comparison = null,
    string? Error = null);

/// <summary>
/// Turns the stored facts of a contract into a projection (docs/PENSION.md step 3). It reads, and it
/// reads only.
///
/// <b>Nothing in this class writes a row.</b> That is not a style preference: step 1 put the
/// guarantee/projection split into the database — only <c>BavSnapshot.Balance</c> reaches the asset,
/// and <c>CK_BavSnapshots_Projection</c> refuses a projected figure that has no basis and no return
/// assumption — precisely so that a scenario the user tried cannot become a stored value. A
/// <c>SaveChangesAsync</c> anywhere in here would be the back door that makes all of that pointless,
/// so every query is <c>AsNoTracking</c> and there is no <c>db.Add</c> in the file.
///
/// What it reads per contract: the newest snapshot (balance, guarantee, annuity), the contribution
/// arrangement in force today, the contract's annuity factor, and the contract's known costs.
///
/// Conventions, because each one is a choice a later reader has to be able to check:
///
/// <list type="bullet">
///   <item><b>The horizon starts today, not at the snapshot's date.</b> The newest balance is used as
///     the starting capital without simulating the months since the statement was written. Filling
///     that gap would mean simulating a period that has already happened, which is the same mistake as
///     recomputing a historical value with today's rate. <c>BalanceAsOf</c> is reported so the user
///     can see how old the fact is.</item>
///   <item><b>Costs are charged on capital only</b>, as an annual percentage —
///     <see cref="BavContractProjection.CostsApplied"/> is that percentage, in percentage points per
///     year, not an amount. Effektivkosten is preferred when the contract states it because it is the
///     one figure that covers every cost channel at once; otherwise the newest
///     <c>percent_of_capital</c> row per kind is summed. A cost on the contribution, on the
///     contribution sum or on the annuity is not a drag on capital and is not folded in: there is no
///     honest conversion, and inventing one would be inventing a cost. The contract's cost list is
///     shown next to the projection for exactly that reason.</item>
///   <item><b>A projected monthly annuity exists only where the contract states an annuity factor</b>
///     (monthly annuity per 10 000 of capital). A monthly pension invented from a capital sum would be
///     the most misleading number on the screen.</item>
///   <item><b>A contract that cannot be projected is returned, not dropped</b>: it goes into
///     <c>Excluded</c> with a <see cref="BavProjectionBlockers"/> reason and stays out of the totals.
///     Silence about it would be the bug.</item>
/// </list>
/// </summary>
public sealed class PensionProjectionStore(
    FullWorthDbContext db,
    CurrencyConverter converter,
    IBavProjectionCalculator calculator)
{
    /// <summary>Two sides that round a half cent in opposite directions may differ by one; more than that is an arithmetic error.</summary>
    private const decimal RoundingTolerance = 0.01m;

    public async Task<BavProjectionOutcome> ProjectAsync(
        Guid userId, Guid spaceId, BavProjectionRequest request, CancellationToken ct)
    {
        var side = await BuildAsync(userId, spaceId, request, ct);
        return new BavProjectionOutcome(side.Result, side.Side?.Result, side.Error);
    }

    /// <summary>
    /// Two projections of the same money, side by side.
    ///
    /// The honesty rule of the whole feature lives here: with the same return, the same costs and the
    /// same horizon, 50 € + 288 € is 338 €. Splitting a contribution across two contracts produces no
    /// extra compound interest, so when the two sides carry the same money under the same assumptions
    /// the capital delta MUST be zero — and if this code ever computes anything else, the arithmetic is
    /// wrong and the rule still holds. It therefore throws rather than returning a number that
    /// contradicts the claim it ships with; a 500 on a read-only projection is recoverable, a lying
    /// projection is not.
    /// </summary>
    public async Task<BavComparisonOutcome> CompareAsync(
        Guid userId, Guid spaceId, BavProjectionRequest left, BavProjectionRequest right, CancellationToken ct)
    {
        var leftSide = await BuildAsync(userId, spaceId, left, ct);
        if (leftSide.Side is null) return new BavComparisonOutcome(leftSide.Result, Error: leftSide.Error);
        var rightSide = await BuildAsync(userId, spaceId, right, ct);
        if (rightSide.Side is null) return new BavComparisonOutcome(rightSide.Result, Error: rightSide.Error);

        var a = leftSide.Side;
        var b = rightSide.Side;

        var capitalDelta = Round(b.ProjectedCapital) - Round(a.ProjectedCapital);
        var annuityDelta = Round(b.ProjectedAnnuity) - Round(a.ProjectedAnnuity);

        // "The same money" is more than an equal contribution: an equal starting capital and an equal
        // horizon are part of it, because a contract that runs three years longer is not the same bet.
        var sameReturn = a.Result.ReturnPercent == b.Result.ReturnPercent;
        var sameMoney = sameReturn
                        && Round(a.StartingCapital) == Round(b.StartingCapital)
                        && Round(a.MonthlyContribution) == Round(b.MonthlyContribution)
                        && Single(a.Horizons, b.Horizons)
                        && a.Result.IsComplete && b.Result.IsComplete
                        && a.Result.Excluded.Count == 0 && b.Result.Excluded.Count == 0;
        var sameCosts = Single(a.CostPercents, b.CostPercents);
        var sameMoneySameAssumptions = sameMoney && sameCosts;

        if (sameMoneySameAssumptions && Math.Abs(capitalDelta) > RoundingTolerance)
            throw new InvalidOperationException(
                "The same money at the same return and the same costs produced a capital difference. " +
                "Splitting a contribution across contracts cannot create compound interest, so the projection arithmetic is wrong.");

        var cause = Cause(a, b, sameMoney, sameCosts, capitalDelta, annuityDelta);

        return new BavComparisonOutcome(
            BavMutationResult.Success,
            new BavComparisonResult(a.Result, b.Result, capitalDelta, annuityDelta, cause, sameMoneySameAssumptions));
    }

    /// <summary>
    /// Names what produced the delta. "The same money" comes first: if the two sides are not the same
    /// money at the same return over the same horizon, they are not a like-for-like comparison of
    /// contracts at all, and attributing the difference to costs or to a guarantee would be a claim
    /// about the contracts that the numbers do not support.
    ///
    /// <see cref="BavDeltaCause.InvestmentConcept"/> is the last resort on purpose: at an equal return
    /// the concept cannot move the capital by itself, so it is named only when the funds behind the two
    /// sides genuinely differ and nothing else explains a delta.
    /// </summary>
    private static IReadOnlyList<string> Cause(
        Side a, Side b, bool sameMoney, bool sameCosts, decimal capitalDelta, decimal annuityDelta)
    {
        if (!sameMoney) return [BavDeltaCause.DifferentAssumptions];

        var causes = new List<string>();
        if (!sameCosts) causes.Add(BavDeltaCause.Costs);
        // A different annuity factor or a different guaranteed capital is a guarantee difference: it
        // moves the monthly annuity without moving a cent of capital, which is exactly the case a
        // capital-only comparison would hide.
        if (!Single(a.AnnuityFactors, b.AnnuityFactors) || Round(a.GuaranteedCapital) != Round(b.GuaranteedCapital))
            causes.Add(BavDeltaCause.Guarantee);
        if (causes.Count == 0 && (capitalDelta != 0m || annuityDelta != 0m)
            && !a.Funds.SequenceEqual(b.Funds, StringComparer.Ordinal))
            causes.Add(BavDeltaCause.InvestmentConcept);

        return causes.Count == 0 ? [BavDeltaCause.None] : causes;
    }

    // ---- the projection itself ----

    private async Task<(BavMutationResult Result, string? Error, Side? Side)> BuildAsync(
        Guid userId, Guid spaceId, BavProjectionRequest request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return (BavMutationResult.NotFound, null, null);

        // The same bound the snapshot writer enforces, so a stored and a simulated projection cannot
        // disagree about what a plausible assumption is.
        if (request.ReturnPercent is < -100m or > 100m)
            return (BavMutationResult.Invalid, "The assumed return must be between -100 and 100 percent.", null);

        var baseCurrency = FxSnapshot.Normalize(await db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == spaceId)
            .Select(space => space.BaseCurrency)
            .SingleOrDefaultAsync(ct) ?? "EUR");

        var all = await Visible(userId, spaceId)
            .OrderBy(contract => contract.ProviderName)
            .ThenBy(contract => contract.CreatedAt)
            .ToListAsync(ct);

        List<BavContract> selected;
        if (request.ContractIds is { Count: > 0 } wanted)
        {
            var known = all.ToDictionary(contract => contract.Id);
            // An id the caller does not have is a 404 like everywhere else in this module: answering
            // with a smaller total instead would hide the mistake inside a plausible number.
            if (wanted.Any(id => !known.ContainsKey(id))) return (BavMutationResult.NotFound, null, null);
            selected = all.Where(contract => wanted.Contains(contract.Id)).ToList();
        }
        // No selection means every contract that still holds capital. A terminated or transferred one
        // has no capital of its own left to grow.
        else selected = all.Where(contract => BavContractStatuses.HoldsCapital(contract.Status)).ToList();

        var ids = selected.Select(contract => contract.Id).ToList();
        var snapshots = await db.BavSnapshots.AsNoTracking()
            .Where(row => ids.Contains(row.BavContractId))
            .ToListAsync(ct);
        var contributions = await db.BavContributions.AsNoTracking()
            .Where(row => ids.Contains(row.BavContractId))
            .ToListAsync(ct);
        var costs = await db.BavCosts.AsNoTracking()
            .Where(row => ids.Contains(row.BavContractId))
            .ToListAsync(ct);
        var allocations = await db.BavInvestmentAllocations.AsNoTracking()
            .Where(row => ids.Contains(row.BavContractId))
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var oldest = snapshots.Count == 0 ? today : snapshots.Min(row => row.EffectiveDate);
        var rates = await converter.PrepareAsync(baseCurrency, oldest, today, ct);

        var included = new List<BavContractProjection>();
        var excluded = new List<BavContractProjection>();
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        var totalBalance = 0m;
        var totalGuaranteedCapital = 0m;
        var totalProjectedCapital = 0m;
        var totalGuaranteedAnnuity = 0m;
        var totalProjectedAnnuity = 0m;
        var totalMonthlyContribution = 0m;
        var costPercents = new SortedSet<decimal>();
        var horizons = new SortedSet<int>();
        var annuityFactors = new SortedSet<decimal>();
        var funds = new List<string>();

        foreach (var contract in selected)
        {
            var current = Current(snapshots, contract.Id);
            var currency = FxSnapshot.Normalize(current?.Currency ?? contract.Currency);
            var retirement = request.RetirementDate ?? contract.RetirementDate;

            // The order of the blockers is the order of the missing facts: without a retirement date
            // there is no horizon at all, so nothing else about the contract matters yet.
            if (retirement is null)
            {
                excluded.Add(Blocked(contract, currency, current, BavProjectionBlockers.NoRetirementDate, request.ReturnPercent));
                continue;
            }
            if (current is null || current.Balance is not { } balance)
            {
                excluded.Add(Blocked(contract, currency, current, BavProjectionBlockers.NoBalance, request.ReturnPercent));
                continue;
            }

            var months = PensionProjectionCalculator.MonthsBetween(today, retirement.Value);
            if (months <= 0)
            {
                excluded.Add(Blocked(contract, currency, current, BavProjectionBlockers.AlreadyDue, request.ReturnPercent));
                continue;
            }

            var (costPercent, costsIncludeEstimates) = Costs(costs, contract, today);

            var running = request.ContinueContributions
                ? Running(contributions, contract.Id, today)
                : null;
            var perYear = BavContributionCycles.PaymentsPerYear(running?.Cycle);
            var monthlyFactor = perYear == 0 ? 0m : perYear / 12m;

            var employeeMonthly = (running?.EmployeeAmount ?? 0m) * monthlyFactor;
            var employerMonthly = ((running?.EmployerAmount ?? 0m) + (running?.EmployerSubsidyAmount ?? 0m)) * monthlyFactor;
            var monthlyStated = employeeMonthly + employerMonthly;

            // The arithmetic runs in the contract's own currency, so a contribution stated in another
            // one has to be brought into it. The rate table is base-currency-only, so the cross rate
            // goes through the base; a missing leg blocks the contract instead of being taken at 1:1.
            var monthlyInContractCurrency = monthlyStated == 0m
                ? 0m
                : Cross(rates, monthlyStated, running!.Currency, currency, today);
            if (monthlyInContractCurrency is null)
            {
                missing.Add(FxSnapshot.Normalize(running!.Currency));
                excluded.Add(Blocked(contract, currency, current, BavProjectionBlockers.MissingRate, request.ReturnPercent));
                continue;
            }

            // A contribution arrangement with an end date stops paying then; the capital keeps
            // developing on its own for the rest of the horizon.
            var payingMonths = running?.ValidUntil is { } until
                ? Math.Min(months, PensionProjectionCalculator.MonthsBetween(today, until))
                : months;

            var projected = calculator.Project(
                balance, monthlyInContractCurrency.Value, payingMonths, request.ReturnPercent, costPercent);
            if (payingMonths < months)
                projected = calculator.Project(projected, 0m, months - payingMonths, request.ReturnPercent, costPercent);

            // Per 10 000 of capital: the factor is what the contract promises per unit, and without one
            // there is no honest way from a capital sum to a monthly pension.
            var projectedAnnuity = contract.GuaranteedAnnuityFactor is { } factor
                ? projected / 10_000m * factor
                : (decimal?)null;

            var employeeAhead = employeeMonthly * payingMonths;
            var employerAhead = employerMonthly * payingMonths;

            var row = new BavContractProjection(
                contract.Id,
                contract.ProviderName,
                currency,
                Round(balance),
                current.EffectiveDate,
                current.GuaranteedCapitalAtRetirement,
                current.GuaranteedMonthlyAnnuity,
                Round(projected),
                projectedAnnuity is null ? null : Round(projectedAnnuity.Value),
                request.ReturnPercent,
                months,
                Round(employeeAhead),
                Round(employerAhead),
                costPercent,
                costsIncludeEstimates);

            // Totals are base-currency. A stated fact keeps the rate of the date it was stated on; a
            // figure computed today can only use today's rate, and there is no rate for a future date
            // to pretend otherwise with.
            var baseBalance = rates.ToBaseOn(balance, currency, current.EffectiveDate);
            var baseGuaranteedCapital = current.GuaranteedCapitalAtRetirement is { } guaranteedCapital
                ? rates.ToBaseOn(guaranteedCapital, currency, current.EffectiveDate)
                : (decimal?)0m;
            var baseGuaranteedAnnuity = current.GuaranteedMonthlyAnnuity is { } guaranteedAnnuity
                ? rates.ToBaseOn(guaranteedAnnuity, currency, current.EffectiveDate)
                : (decimal?)0m;
            var baseProjected = rates.ToBaseOn(projected, currency, today);
            var baseProjectedAnnuity = projectedAnnuity is { } annuity
                ? rates.ToBaseOn(annuity, currency, today)
                : (decimal?)0m;
            var baseMonthly = rates.ToBaseOn(monthlyInContractCurrency.Value, currency, today);

            if (baseBalance is null || baseGuaranteedCapital is null || baseGuaranteedAnnuity is null
                || baseProjected is null || baseProjectedAnnuity is null || baseMonthly is null)
            {
                // A total short a whole contract is worse than a total that says it is incomplete.
                missing.Add(currency);
                excluded.Add(row with { Blocker = BavProjectionBlockers.MissingRate });
                continue;
            }

            included.Add(row);
            totalBalance += baseBalance.Value;
            totalGuaranteedCapital += baseGuaranteedCapital.Value;
            totalGuaranteedAnnuity += baseGuaranteedAnnuity.Value;
            totalProjectedCapital += baseProjected.Value;
            totalProjectedAnnuity += baseProjectedAnnuity.Value;
            totalMonthlyContribution += baseMonthly.Value;
            costPercents.Add(costPercent);
            horizons.Add(months);
            if (contract.GuaranteedAnnuityFactor is { } stated) annuityFactors.Add(stated);
            funds.AddRange(Funds(allocations, contract.Id));
        }

        funds.Sort(StringComparer.Ordinal);

        var result = new BavProjectionResult(
            baseCurrency,
            request.ReturnPercent,
            included,
            Round(totalBalance),
            Round(totalGuaranteedCapital),
            Round(totalProjectedCapital),
            Round(totalGuaranteedAnnuity),
            Round(totalProjectedAnnuity),
            missing.Count == 0,
            missing.ToList(),
            excluded);

        return (BavMutationResult.Success, null, new Side(
            result,
            totalBalance,
            totalMonthlyContribution,
            totalProjectedCapital,
            totalProjectedAnnuity,
            totalGuaranteedCapital,
            costPercents,
            horizons,
            annuityFactors,
            funds));
    }

    /// <summary>
    /// The annual cost percentage the projection charges on capital, plus whether an estimate is in it.
    ///
    /// Only <c>percent_of_capital</c> rows can enter: that is the one basis that is a drag on capital
    /// per year. An <c>incurred</c> cost is left out because it has already been taken off the balance
    /// the projection starts from, and charging it again would take it twice. A paid-up contract keeps
    /// only the costs that say they continue — which is what makes "beitragsfrei ≠ cost-free" arithmetic
    /// rather than a hope.
    ///
    /// Effektivkosten is an aggregate <b>over</b> the other kinds, so it replaces their sum instead of
    /// joining it. Where it is absent the newest row per kind is summed — newest per kind, because two
    /// dated rows for the same kind are a correction and its predecessor, not two costs.
    /// </summary>
    private static (decimal Percent, bool IncludesEstimates) Costs(
        IEnumerable<BavCost> costs, BavContract contract, DateOnly today)
    {
        var paidUp = contract.Status == BavContractStatuses.PaidUp;
        var applicable = costs.Where(row => row.BavContractId == contract.Id
                                            && row.Basis == BavCostBases.PercentOfCapital
                                            && row.Percent is not null
                                            && row.Timing != BavCostTimings.Incurred
                                            && (row.AppliesUntilDate is null || row.AppliesUntilDate >= today)
                                            && (!paidUp || row.ContinuesWhenPaidUp))
            .ToList();
        if (applicable.Count == 0) return (0m, false);

        var aggregate = applicable
            .Where(row => BavCostKinds.IsAggregate(row.Kind))
            .OrderByDescending(row => row.EffectiveDate).ThenByDescending(row => row.CreatedAt)
            .FirstOrDefault();

        List<BavCost> used = aggregate is not null
            ? [aggregate]
            : applicable
                .Where(row => !BavCostKinds.IsAggregate(row.Kind))
                .GroupBy(row => row.Kind, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(row => row.EffectiveDate).ThenByDescending(row => row.CreatedAt)
                    .First())
                .ToList();

        // An estimate is applied AND reported as one. Leaving it out would understate the cost, which
        // is the direction that flatters the projection.
        return (used.Sum(row => row.Percent ?? 0m), used.Any(row => row.IsEstimated));
    }

    private static BavContractProjection Blocked(
        BavContract contract, string currency, BavSnapshot? current, string blocker, decimal returnPercent) =>
        new(
            contract.Id,
            contract.ProviderName,
            currency,
            current?.Balance is { } balance ? Round(balance) : null,
            current?.EffectiveDate,
            current?.GuaranteedCapitalAtRetirement,
            current?.GuaranteedMonthlyAnnuity,
            // No projected figure at all: a blocked contract has no number to show, and a zero would
            // read as one.
            null,
            null,
            returnPercent,
            0,
            0m,
            0m,
            0m,
            false,
            blocker);

    /// <summary>
    /// The newest accepted snapshot. <c>IsCurrent</c> first and the newest effective date as the
    /// fallback, the same order <c>PensionStore</c> uses — "current" is the newest effective date and
    /// not the newest insert, so entering last year's statement after this year's changes nothing.
    /// </summary>
    private static BavSnapshot? Current(IEnumerable<BavSnapshot> snapshots, Guid contractId)
    {
        var own = snapshots.Where(row => row.BavContractId == contractId).ToList();
        return own.FirstOrDefault(row => row.IsCurrent)
               ?? own.OrderByDescending(row => row.EffectiveDate).ThenByDescending(row => row.CreatedAt).FirstOrDefault();
    }

    /// <summary>The arrangement in force on a date. A one-off contribution is never "in force".</summary>
    private static BavContribution? Running(IEnumerable<BavContribution> rows, Guid contractId, DateOnly on) =>
        rows.Where(row => row.BavContractId == contractId
                          && row.Cycle != BavContributionCycles.OneOff
                          && row.ValidFrom <= on
                          && (row.ValidUntil is null || row.ValidUntil >= on))
            .OrderByDescending(row => row.ValidFrom)
            .ThenByDescending(row => row.CreatedAt)
            .FirstOrDefault();

    /// <summary>
    /// The fund shape behind a contract, as of its newest allocation date. Only used to tell two
    /// investment concepts apart in a comparison — never to value anything.
    /// </summary>
    private static IEnumerable<string> Funds(IEnumerable<BavInvestmentAllocation> allocations, Guid contractId)
    {
        var own = allocations.Where(row => row.BavContractId == contractId).ToList();
        if (own.Count == 0) return [];
        var newest = own.Max(row => row.EffectiveDate);
        return own.Where(row => row.EffectiveDate == newest)
            .Select(row => $"{row.Isin ?? row.FundName}|{row.WeightPercent}|{row.OngoingChargesPercent}");
    }

    /// <summary>
    /// Converts between two non-base currencies through the base, because the rate table only holds
    /// base-relative fixings. Null when either leg is missing — never 1:1.
    /// </summary>
    private static decimal? Cross(FxSnapshot rates, decimal amount, string from, string to, DateOnly on)
    {
        if (FxSnapshot.Normalize(from) == FxSnapshot.Normalize(to)) return amount;
        var amountInBase = rates.ToBaseOn(amount, from, on);
        var unitInBase = rates.ToBaseOn(1m, to, on);
        if (amountInBase is null || unitInBase is null || unitInBase.Value == 0m) return null;
        return amountInBase.Value / unitInBase.Value;
    }

    private Task<bool> IsMemberAsync(Guid userId, Guid spaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == spaceId && member.UserId == userId, ct);

    private IQueryable<BavContract> Visible(Guid userId, Guid spaceId) =>
        db.BavContracts.AsNoTracking().Where(contract =>
            contract.FullWorthSpaceId == spaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == spaceId && member.UserId == userId));

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>True when both sets describe one and the same single value — the test for "same assumption".</summary>
    private static bool Single<T>(IReadOnlyCollection<T> left, IReadOnlyCollection<T> right) =>
        left.Count <= 1 && right.Count <= 1 && left.SequenceEqual(right);

    /// <summary>
    /// One side of a comparison: the result the caller sees, plus the base-currency facts and the
    /// assumption sets the comparison has to reason about. Unrounded on purpose — the delta is only
    /// exactly zero for the same money if the totals were not each rounded first.
    /// </summary>
    private sealed record Side(
        BavProjectionResult Result,
        decimal StartingCapital,
        decimal MonthlyContribution,
        decimal ProjectedCapital,
        decimal ProjectedAnnuity,
        decimal GuaranteedCapital,
        IReadOnlyCollection<decimal> CostPercents,
        IReadOnlyCollection<int> Horizons,
        IReadOnlyCollection<decimal> AnnuityFactors,
        IReadOnlyList<string> Funds);
}
