using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Pension;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FullWorth.Backend.Tests.Pension.PensionContractIntegrationTests;

namespace FullWorth.Backend.Tests.Pension;

/// <summary>
/// The projection arithmetic (docs/PENSION.md step 3). The calculator half needs no database on
/// purpose: every figure a user is shown here is an assumption, so the arithmetic behind it has to be
/// checkable by hand, and it is these tests that pin the three conventions the numbers rest on —
/// twelfth-root compounding, a contribution at the start of the month, and a cost percentage taken off
/// the return in percentage points.
/// </summary>
public sealed class PensionProjectionCalculatorTests
{
    private static readonly PensionProjectionCalculator Calculator = new();

    /// <summary>
    /// A balance, no contributions, a round return and a whole number of years: 10 000 € at 5 % for two
    /// years is 10 000 · 1.05² and nothing else. If this drifts, everything below it is guesswork.
    /// </summary>
    [Fact]
    public void A_round_return_over_whole_years_lands_on_the_cent()
    {
        Assert.Equal(11_025.00m, Cents(Calculator.Project(10_000m, 0m, 24, 5m, 0m)));
        Assert.Equal(10_500.00m, Cents(Calculator.Project(10_000m, 0m, 12, 5m, 0m)));
        Assert.Equal(11_576.25m, Cents(Calculator.Project(10_000m, 0m, 36, 5m, 0m)));
    }

    /// <summary>
    /// The decision this whole file exists for. Twelve months of monthly compounding must reproduce the
    /// annual assumption exactly: 7 % a year is 7 % after twelve months. The <c>annual / 12</c>
    /// shortcut instead produces 7.229 % — 0.23 percentage points a year that appear on screen and
    /// never in the account.
    /// </summary>
    [Fact]
    public void Twelve_months_at_seven_percent_grow_by_exactly_seven_percent()
    {
        Assert.Equal(10_700.00m, Cents(Calculator.Project(10_000m, 0m, 12, 7m, 0m)));

        // What the shortcut would have produced, computed the shortcut's way.
        var naiveMonthly = 1m + 7m / 100m / 12m;
        var naive = 10_000m;
        for (var month = 0; month < 12; month++) naive *= naiveMonthly;

        Assert.Equal(10_722.90m, Cents(naive));
        Assert.True(naive - Calculator.Project(10_000m, 0m, 12, 7m, 0m) > 22m,
            "annual/12 overstates a 7 % year by more than 22 € on 10 000 €, which is why it is not used.");

        // The same statement about the factor itself: it compounds to the annual factor, not past it.
        var factor = PensionProjectionCalculator.MonthlyFactor(7m, 0m);
        var compounded = 1m;
        for (var month = 0; month < 12; month++) compounded *= factor;
        Assert.Equal(1.0700000000m, Math.Round(compounded, 10));
    }

    /// <summary>
    /// The contribution convention: a monthly bAV premium is debited at the start of the period and
    /// earns that whole month. End-of-month posting is exactly one month of growth cheaper on every
    /// payment, so the two differ by precisely one monthly factor — which is how this test can pin the
    /// choice rather than merely describe it.
    /// </summary>
    [Fact]
    public void A_contribution_is_paid_at_the_start_of_the_month_and_earns_it()
    {
        var factor = PensionProjectionCalculator.MonthlyFactor(6m, 0m);

        // One month, one payment: at the start of the month it has grown once. At the end it would
        // still be exactly the 100 € that were paid in.
        Assert.Equal(Cents(100m * factor), Cents(Calculator.Project(0m, 100m, 1, 6m, 0m)));
        Assert.True(Calculator.Project(0m, 100m, 1, 6m, 0m) > 100m);

        // Over a year the whole stream is one month of growth ahead of the end-of-month convention.
        var endOfMonth = 0m;
        for (var month = 0; month < 12; month++) endOfMonth = endOfMonth * factor + 100m;

        var startOfMonth = Calculator.Project(0m, 100m, 12, 6m, 0m);
        Assert.True(startOfMonth > endOfMonth);
        Assert.Equal(Cents(endOfMonth * factor), Cents(startOfMonth));
    }

    /// <summary>
    /// A cost percentage is taken off the return in percentage points, which is the definition of
    /// Effektivkosten rather than an approximation of it: 7 % gross with 1 % costs is a flat 6 %. And
    /// zero costs change nothing at all — a cost channel that leaks when it is empty would be worse
    /// than no cost channel.
    /// </summary>
    [Fact]
    public void A_cost_percentage_reduces_the_result_and_zero_costs_change_nothing()
    {
        Assert.Equal(10_600.00m, Cents(Calculator.Project(10_000m, 0m, 12, 7m, 1m)));
        Assert.Equal(Cents(Calculator.Project(10_000m, 0m, 12, 6m, 0m)),
            Cents(Calculator.Project(10_000m, 0m, 12, 7m, 1m)));

        Assert.Equal(10_700.00m, Cents(Calculator.Project(10_000m, 0m, 12, 7m, 0m)));
        Assert.Equal(Calculator.Project(10_000m, 250m, 120, 5m, 0m),
            Calculator.Project(10_000m, 250m, 120, 5m, 0m));
        Assert.True(Calculator.Project(10_000m, 250m, 120, 5m, 0.75m)
                    < Calculator.Project(10_000m, 250m, 120, 5m, 0m));
    }

    /// <summary>No time has passed, so the balance comes back exactly as it went in — not even rounded.</summary>
    [Fact]
    public void Zero_months_returns_the_balance_untouched()
    {
        Assert.Equal(1_234.567m, Calculator.Project(1_234.567m, 500m, 0, 7m, 0.5m));
        Assert.Equal(1_234.567m, Calculator.Project(1_234.567m, 500m, -12, 7m, 0.5m));
    }

    /// <summary>
    /// A negative return shrinks the capital and stops there. Positive money in cannot come out
    /// negative, and a total loss (−100 % a year) is nothing rather than a negative balance.
    /// </summary>
    [Fact]
    public void A_negative_return_shrinks_the_capital_without_inventing_a_debt()
    {
        var shrunk = Calculator.Project(10_000m, 100m, 12, -20m, 0m);
        Assert.True(shrunk > 0m);
        Assert.True(shrunk < 10_000m + 1_200m);
        // 8 000 € left of the balance plus a year of payments that also lost ground.
        Assert.True(shrunk > 8_000m);

        Assert.Equal(0m, Calculator.Project(10_000m, 100m, 12, -100m, 0m));
        Assert.Equal(0m, Calculator.Project(10_000m, 100m, 12, 5m, 105m));
    }

    /// <summary>A part month is not a month of growth: counting it would invent interest.</summary>
    [Fact]
    public void Months_between_counts_whole_months_only()
    {
        Assert.Equal(0, PensionProjectionCalculator.MonthsBetween(new(2026, 1, 15), new(2026, 2, 14)));
        Assert.Equal(1, PensionProjectionCalculator.MonthsBetween(new(2026, 1, 15), new(2026, 2, 15)));
        Assert.Equal(12, PensionProjectionCalculator.MonthsBetween(new(2026, 1, 31), new(2027, 1, 31)));
        Assert.Equal(0, PensionProjectionCalculator.MonthsBetween(new(2026, 5, 1), new(2026, 5, 1)));
        Assert.Equal(0, PensionProjectionCalculator.MonthsBetween(new(2026, 5, 1), new(2020, 5, 1)));
    }

    private static decimal Cents(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The projection store: the rules that keep a projection from ever becoming a value, and the
/// comparison that must not invent an advantage.
///
/// These need Postgres (<c>FULLWORTH_TEST_POSTGRES</c>). The store is constructed directly rather than
/// reached over HTTP, because the routes are mapped by the host and the rules under test are the
/// store's own.
/// </summary>
public sealed class PensionProjectionStoreTests
{
    private static readonly DateOnly Retirement = new(2056, 7, 1);

    /// <summary>
    /// The rule the whole feature rests on: running a projection writes nothing. Not a snapshot, not a
    /// contribution, not an asset value — and no stored snapshot gains a projected figure, because a
    /// projected figure that sits in a row is indistinguishable from a guarantee.
    /// </summary>
    [Fact]
    public async Task A_projection_writes_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Direktversicherer A",
            policyNumber = "DV-100",
            implementationRoute = "direct_insurance",
            retirementDate = Retirement.ToString("yyyy-MM-dd"),
            currency = "EUR"
        });
        await SnapshotAsync(client, scenario, contractId, new { effectiveDate = "2026-01-01", balance = 30_000m });
        await ContributionAsync(client, scenario, contractId, new
        {
            validFrom = "2020-01-01",
            cycle = "monthly",
            employeeAmount = 169m,
            employerSubsidyAmount = 25.35m
        });

        var before = await CountsAsync(factory);

        var outcome = await ProjectAsync(factory, scenario, new BavProjectionRequest(ReturnPercent: 5m));
        Assert.Equal(BavMutationResult.Success, outcome.Result);
        var projection = Assert.Single(outcome.Projection!.Contracts);
        // It really did project something, so "nothing changed" is a statement about a real run.
        Assert.NotNull(projection.ProjectedCapital);
        Assert.True(projection.ProjectedCapital > 30_000m);

        Assert.Equal(before, await CountsAsync(factory));

        await factory.SeedAsync(async db =>
        {
            var snapshots = await db.BavSnapshots.AsNoTracking().ToListAsync();
            Assert.All(snapshots, snapshot =>
            {
                Assert.Null(snapshot.ProjectedCapitalAtRetirement);
                Assert.Null(snapshot.ProjectedMonthlyAnnuity);
                Assert.Null(snapshot.ProjectionBasis);
                Assert.Null(snapshot.ProjectionReturnPercent);
            });
            // The asset still carries the snapshot balance and not the projection.
            var assets = await db.Assets.AsNoTracking().ToListAsync();
            Assert.All(assets, asset => Assert.Equal(30_000m, asset.CurrentValue));
        });
    }

    /// <summary>
    /// A contract without a retirement date has no horizon, so it cannot be projected. It comes back
    /// named in <c>Excluded</c> and stays out of the totals: dropping it silently would make a smaller
    /// total look like a complete one.
    /// </summary>
    [Fact]
    public async Task A_contract_without_a_retirement_date_is_excluded_and_says_why()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Pensionskasse ohne Datum",
            policyNumber = "PK-1",
            implementationRoute = "pension_fund",
            currency = "EUR"
        });
        await SnapshotAsync(client, scenario, contractId, new { effectiveDate = "2026-01-01", balance = 12_000m });

        var result = (await ProjectAsync(factory, scenario, new BavProjectionRequest(ReturnPercent: 5m))).Projection!;

        Assert.Empty(result.Contracts);
        var excluded = Assert.Single(result.Excluded);
        Assert.Equal(BavProjectionBlockers.NoRetirementDate, excluded.Blocker);
        Assert.Equal(contractId, excluded.ContractId);
        // The balance it does have is still reported; only the projection is missing.
        Assert.Equal(12_000m, excluded.CurrentBalance);
        Assert.Null(excluded.ProjectedCapital);
        Assert.Equal(0m, result.TotalProjectedCapital);
        Assert.Equal(0m, result.TotalCurrentBalance);
    }

    /// <summary>
    /// A guaranteed figure is a fact about the contract and is handed through exactly as stated. It
    /// never enters, and is never replaced by, the projected one — they are two different promises.
    /// </summary>
    [Fact]
    public async Task A_guaranteed_figure_is_passed_through_and_never_mixed_into_the_projection()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Direktversicherer G",
            policyNumber = "DV-G",
            implementationRoute = "direct_insurance",
            retirementDate = Retirement.ToString("yyyy-MM-dd"),
            guaranteedAnnuityFactor = 30m,
            currency = "EUR"
        });
        await SnapshotAsync(client, scenario, contractId, new
        {
            effectiveDate = "2026-01-01",
            balance = 40_000m,
            guaranteedCapitalAtRetirement = 62_500.55m,
            guaranteedMonthlyAnnuity = 187.50m
        });

        var result = (await ProjectAsync(factory, scenario, new BavProjectionRequest(ReturnPercent: 5m))).Projection!;
        var row = Assert.Single(result.Contracts);

        Assert.Equal(62_500.55m, row.GuaranteedCapital);
        Assert.Equal(187.50m, row.GuaranteedMonthlyAnnuity);
        Assert.Equal(62_500.55m, result.TotalGuaranteedCapital);
        Assert.Equal(187.50m, result.TotalGuaranteedMonthlyAnnuity);

        Assert.NotNull(row.ProjectedCapital);
        Assert.NotEqual(row.GuaranteedCapital, row.ProjectedCapital);
        Assert.NotEqual(row.GuaranteedMonthlyAnnuity, row.ProjectedMonthlyAnnuity);
        // Per 10 000 of capital, which is what the stated factor means. Compared with a tolerance of one
        // cent rather than with a decimal-place precision: the store derives the annuity from the
        // UNROUNDED capital and rounds once, while this line re-derives it from the rounded capital, so
        // the two legitimately differ in the third decimal - and 512.345460 vs 512.35 happens to straddle
        // a rounding boundary, which is exactly where a decimal-place comparison reports a failure that
        // is not one.
        var expectedAnnuity = row.ProjectedCapital!.Value / 10_000m * 30m;
        Assert.True(Math.Abs(expectedAnnuity - row.ProjectedMonthlyAnnuity!.Value) <= 0.01m,
            $"annuity {row.ProjectedMonthlyAnnuity} is not the capital {row.ProjectedCapital} times the stated factor (expected ~{expectedAnnuity})");
    }

    /// <summary>
    /// Without a stated annuity factor there is no way from a capital sum to a monthly pension, and
    /// inventing one would be the most misleading number on the screen. The capital is still projected.
    /// </summary>
    [Fact]
    public async Task No_annuity_factor_means_no_projected_monthly_annuity()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Pensionsfonds ohne Faktor",
            policyNumber = "PF-1",
            implementationRoute = "pension_scheme",
            retirementDate = Retirement.ToString("yyyy-MM-dd"),
            currency = "EUR"
        });
        await SnapshotAsync(client, scenario, contractId, new { effectiveDate = "2026-01-01", balance = 25_000m });

        var result = (await ProjectAsync(factory, scenario, new BavProjectionRequest(ReturnPercent: 5m))).Projection!;
        var row = Assert.Single(result.Contracts);

        Assert.NotNull(row.ProjectedCapital);
        Assert.Null(row.ProjectedMonthlyAnnuity);
        Assert.Equal(0m, result.TotalProjectedMonthlyAnnuity);
    }

    /// <summary>
    /// A missing rate makes the result incomplete and names the currency. It never shortens the total
    /// quietly and never assumes 1:1 — the contract is excluded and says which rate was missing.
    /// </summary>
    [Fact]
    public async Task A_missing_rate_makes_the_result_incomplete_and_names_the_currency()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Swiss Pensionskasse",
            policyNumber = "CH-1",
            implementationRoute = "pension_fund",
            retirementDate = Retirement.ToString("yyyy-MM-dd"),
            currency = "CHF"
        });
        await SnapshotAsync(client, scenario, contractId, new { effectiveDate = "2026-01-01", balance = 20_000m });

        var result = (await ProjectAsync(factory, scenario, new BavProjectionRequest(ReturnPercent: 5m))).Projection!;

        Assert.False(result.IsComplete);
        Assert.Contains("CHF", result.MissingCurrencies);
        Assert.Empty(result.Contracts);
        Assert.Equal(BavProjectionBlockers.MissingRate, Assert.Single(result.Excluded).Blocker);
        Assert.Equal(0m, result.TotalProjectedCapital);
    }

    /// <summary>
    /// Effektivkosten is an aggregate OVER the other cost kinds, so it is used INSTEAD of their sum.
    /// Adding it to them would charge every component beneath it twice.
    /// </summary>
    [Fact]
    public async Task Effektivkosten_replaces_the_sum_of_the_components()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var components = await ProjectedWithCostsAsync(factory, client, scenario, "KO-1", costs:
        [
            new { effectiveDate = "2024-01-01", kind = "administration_on_capital", basis = "percent_of_capital", percent = 0.3m },
            new { effectiveDate = "2024-01-01", kind = "fund", basis = "percent_of_capital", percent = 0.5m }
        ]);
        Assert.Equal(0.8m, components.CostsApplied);

        using var aggregateFactory = new BackendWebApplicationFactory();
        var aggregateScenario = await SeedAsync(aggregateFactory);
        using var aggregateClient = aggregateFactory.CreateClient();

        var aggregate = await ProjectedWithCostsAsync(aggregateFactory, aggregateClient, aggregateScenario, "KO-2", costs:
        [
            new { effectiveDate = "2024-01-01", kind = "administration_on_capital", basis = "percent_of_capital", percent = 0.3m },
            new { effectiveDate = "2024-01-01", kind = "fund", basis = "percent_of_capital", percent = 0.5m },
            new { effectiveDate = "2024-01-01", kind = "effective_cost", basis = "percent_of_capital", percent = 1.1m }
        ]);

        // 1.1 (the figure that describes the whole contract), not 1.9 (0.3 + 0.5 + 1.1).
        Assert.Equal(1.1m, aggregate.CostsApplied);
        Assert.NotEqual(1.9m, aggregate.CostsApplied);
        Assert.False(aggregate.CostsIncludeEstimates);
    }

    /// <summary>An estimated cost is applied AND reported as an estimate; leaving it out would flatter the projection.</summary>
    [Fact]
    public async Task An_estimated_cost_is_applied_and_reported_as_estimated()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var row = await ProjectedWithCostsAsync(factory, client, scenario, "KO-3", costs:
        [
            new
            {
                effectiveDate = "2024-01-01",
                kind = "fund",
                basis = "percent_of_capital",
                percent = 0.6m,
                isEstimated = true,
                estimateBasis = "TER des Zielfonds aus dem Factsheet"
            }
        ]);

        Assert.Equal(0.6m, row.CostsApplied);
        Assert.True(row.CostsIncludeEstimates);
    }

    /// <summary>
    /// The comparison rule, and the one that is easiest to lose: with the same return and the same
    /// costs, 50 € + 288 € is 338 €. Splitting a contribution across two contracts produces no extra
    /// compound interest, so the capital delta is exactly zero and there is nothing to attribute.
    /// Change only the costs and the delta appears — and the comparison names costs as its cause.
    /// </summary>
    [Fact]
    public async Task Splitting_a_contribution_across_contracts_creates_no_advantage()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var half = await ContractWithAsync(client, scenario, "SPL-A", 1_000m, 50m, 0.5m);
        var rest = await ContractWithAsync(client, scenario, "SPL-B", 2_000m, 288m, 0.5m);
        var whole = await ContractWithAsync(client, scenario, "SPL-C", 3_000m, 338m, 0.5m);
        var dearer = await ContractWithAsync(client, scenario, "SPL-D", 3_000m, 338m, 1.5m);

        var split = new BavProjectionRequest(ContractIds: [half, rest], ReturnPercent: 5m);
        var single = new BavProjectionRequest(ContractIds: [whole], ReturnPercent: 5m);

        var fair = await CompareAsync(factory, scenario, split, single);
        Assert.Equal(BavMutationResult.Success, fair.Result);
        var comparison = fair.Comparison!;

        Assert.True(comparison.SameMoneySameAssumptions);
        Assert.Equal(0m, comparison.CapitalDelta);
        Assert.Equal(0m, comparison.AnnuityDelta);
        Assert.Equal([BavDeltaCause.None], comparison.Cause);
        // And the two sides really did project the same money, so the zero is not a zero of nothing.
        Assert.True(comparison.Left.TotalProjectedCapital > 3_000m);
        Assert.Equal(comparison.Left.TotalProjectedCapital, comparison.Right.TotalProjectedCapital);

        var unequal = (await CompareAsync(factory, scenario, split,
            new BavProjectionRequest(ContractIds: [dearer], ReturnPercent: 5m))).Comparison!;

        Assert.False(unequal.SameMoneySameAssumptions);
        Assert.NotEqual(0m, unequal.CapitalDelta);
        // The dearer contract ends up behind, so the right side is the smaller one.
        Assert.True(unequal.CapitalDelta < 0m);
        Assert.Equal([BavDeltaCause.Costs], unequal.Cause);
    }

    /// <summary>
    /// Two sides that do not even share a return are not a comparison of contracts, and saying "costs"
    /// about that difference would be a claim about the contracts the numbers do not support.
    /// </summary>
    [Fact]
    public async Task Different_returns_are_named_as_different_assumptions()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await ContractWithAsync(client, scenario, "ASM-1", 5_000m, 100m, 0.5m);
        var comparison = (await CompareAsync(factory, scenario,
            new BavProjectionRequest(ContractIds: [contractId], ReturnPercent: 3m),
            new BavProjectionRequest(ContractIds: [contractId], ReturnPercent: 7m))).Comparison!;

        Assert.False(comparison.SameMoneySameAssumptions);
        Assert.Equal([BavDeltaCause.DifferentAssumptions], comparison.Cause);
        Assert.True(comparison.CapitalDelta > 0m);
    }

    /// <summary>A projection is bounded by the same assumption range a stored one is, so the two cannot disagree.</summary>
    [Fact]
    public async Task An_impossible_return_is_refused()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);

        var outcome = await ProjectAsync(factory, scenario, new BavProjectionRequest(ReturnPercent: 250m));
        Assert.Equal(BavMutationResult.Invalid, outcome.Result);
        Assert.Null(outcome.Projection);
        Assert.NotNull(outcome.Error);
    }

    /// <summary>A non-member gets not-found, so the existence of a pension contract does not leak.</summary>
    [Fact]
    public async Task A_non_member_gets_not_found()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);

        var outcome = await RunAsync(factory, store =>
            store.ProjectAsync(scenario.Outside, scenario.Space, new BavProjectionRequest(ReturnPercent: 5m), default));
        Assert.Equal(BavMutationResult.NotFound, outcome.Result);
        Assert.Null(outcome.Projection);
    }

    // ---- helpers ----

    private sealed record Counts(int Snapshots, int Contributions, int Costs, int Assets, int Contracts);

    private static async Task<Counts> CountsAsync(BackendWebApplicationFactory factory)
    {
        Counts counts = null!;
        await factory.SeedAsync(async db => counts = new Counts(
            await db.BavSnapshots.CountAsync(),
            await db.BavContributions.CountAsync(),
            await db.BavCosts.CountAsync(),
            await db.Assets.CountAsync(),
            await db.BavContracts.CountAsync()));
        return counts;
    }

    private static async Task<T> RunAsync<T>(
        BackendWebApplicationFactory factory, Func<PensionProjectionStore, Task<T>> act)
    {
        T result = default!;
        await factory.SeedAsync(async (FullWorthDbContext db) =>
            result = await act(new PensionProjectionStore(db, new CurrencyConverter(db), new PensionProjectionCalculator())));
        return result;
    }

    private static Task<BavProjectionOutcome> ProjectAsync(
        BackendWebApplicationFactory factory, Scenario scenario, BavProjectionRequest request) =>
        RunAsync(factory, store => store.ProjectAsync(scenario.Owner, scenario.Space, request, default));

    private static Task<BavComparisonOutcome> CompareAsync(
        BackendWebApplicationFactory factory, Scenario scenario,
        BavProjectionRequest left, BavProjectionRequest right) =>
        RunAsync(factory, store => store.CompareAsync(scenario.Owner, scenario.Space, left, right, default));

    private static async Task SnapshotAsync(
        System.Net.Http.HttpClient client, Scenario scenario, Guid contractId, object body)
    {
        using var response = await PostAsync(client, scenario, $"/api/pension/contracts/{contractId}/snapshots", body);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task ContributionAsync(
        System.Net.Http.HttpClient client, Scenario scenario, Guid contractId, object body)
    {
        using var response = await PostAsync(client, scenario, $"/api/pension/contracts/{contractId}/contributions", body);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task CostAsync(
        System.Net.Http.HttpClient client, Scenario scenario, Guid contractId, object body)
    {
        using var response = await PostAsync(client, scenario, $"/api/pension/contracts/{contractId}/costs", body);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>A contract with a balance, a running monthly contribution and one capital cost percentage.</summary>
    private static async Task<Guid> ContractWithAsync(
        System.Net.Http.HttpClient client, Scenario scenario, string policyNumber,
        decimal balance, decimal monthlyContribution, decimal costPercent)
    {
        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = $"Versicherer {policyNumber}",
            policyNumber,
            implementationRoute = "direct_insurance",
            retirementDate = Retirement.ToString("yyyy-MM-dd"),
            currency = "EUR"
        });
        await SnapshotAsync(client, scenario, contractId, new { effectiveDate = "2026-01-01", balance });
        await ContributionAsync(client, scenario, contractId, new
        {
            validFrom = "2020-01-01",
            cycle = "monthly",
            employeeAmount = monthlyContribution
        });
        await CostAsync(client, scenario, contractId, new
        {
            effectiveDate = "2020-01-01",
            kind = "administration_on_capital",
            basis = "percent_of_capital",
            percent = costPercent,
            timing = "ongoing"
        });
        return contractId;
    }

    private static async Task<BavContractProjection> ProjectedWithCostsAsync(
        BackendWebApplicationFactory factory, System.Net.Http.HttpClient client, Scenario scenario,
        string policyNumber, object[] costs)
    {
        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = $"Versicherer {policyNumber}",
            policyNumber,
            implementationRoute = "direct_insurance",
            retirementDate = Retirement.ToString("yyyy-MM-dd"),
            currency = "EUR"
        });
        await SnapshotAsync(client, scenario, contractId, new { effectiveDate = "2026-01-01", balance = 10_000m });
        foreach (var cost in costs) await CostAsync(client, scenario, contractId, cost);

        var result = (await ProjectAsync(factory, scenario, new BavProjectionRequest(ReturnPercent: 5m))).Projection!;
        return Assert.Single(result.Contracts);
    }
}
