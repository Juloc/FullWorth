using FullWorth.Backend.Modules.Reconciliation;

namespace FullWorth.Backend.Tests.Reconciliation;

/// <summary>
/// Plain unit tests for the dedup rule extracted out of
/// <c>FinancialReconciliationReportService.CashflowAvailableAsync</c> for #139 - no database, no HTTP.
/// </summary>
public sealed class ForecastDedupTests
{
    [Fact]
    public void AlreadyLinkedContracts_returns_empty_set_when_load_is_null()
    {
        Assert.Empty(ForecastDedup.AlreadyLinkedContracts(null));
    }

    [Fact]
    public void AlreadyLinkedContracts_collects_contract_ids_across_every_item()
    {
        var contractA = Guid.NewGuid();
        var contractB = Guid.NewGuid();
        var load = new CanonicalContributionLoad(
            [
                Contribution(contractIds: new HashSet<Guid> { contractA }),
                Contribution(contractIds: new HashSet<Guid> { contractB }),
                Contribution(contractIds: new HashSet<Guid>())
            ],
            "EUR", false, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var result = ForecastDedup.AlreadyLinkedContracts(load);

        Assert.Equal(new HashSet<Guid> { contractA, contractB }, result);
    }

    [Fact]
    public void PendingIncomeParties_normalizes_and_prefers_normalized_counterparty()
    {
        var result = ForecastDedup.PendingIncomeParties(
        [
            ("Employer GmbH", null),
            (null, "Other Corp."),
            (null, null)
        ]);

        Assert.Equal(2, result.Count);
        Assert.Contains("EMPLOYER GMBH", result);
        Assert.Contains("OTHER CORP", result);
    }

    [Fact]
    public void PendingIncomeParties_lookup_is_case_insensitive()
    {
        var result = ForecastDedup.PendingIncomeParties([("Employer GmbH", null)]);

        Assert.Contains("employer gmbh", result);
    }

    private static CanonicalContribution Contribution(HashSet<Guid> contractIds) => new(
        Guid.NewGuid(), Guid.NewGuid(), null, new DateOnly(2026, 1, 15), "Merchant", "EUR",
        -10m, -10m, ContributionKinds.Expense, new HashSet<Guid>(), contractIds, 0m);
}
