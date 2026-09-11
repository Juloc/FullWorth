using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// Recurring income appears on the screen that is about recurring money.
///
/// The backend detected it all along — <c>GET /api/income-schedules/detection</c> groups positive
/// bookings by counterparty and cadence the same way the cost side does, with the same confidence and
/// the same memory of what was dismissed — and <b>nothing in the frontend ever called it</b>. A salary
/// was therefore invisible: not in a list, not as a suggestion, and not in the forward preview, which
/// reads its monthly income from exactly these schedules.
/// </summary>
public sealed class ContractsIncomePanelTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public ContractsIncomePanelTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Contracts_screen_reads_income_schedules_and_their_detection()
    {
        var contracts = Read("features/contracts.js");

        Assert.Contains("api/income-schedules'", contracts);
        Assert.Contains("api/income-schedules/detection'", contracts);
        Assert.Contains("api/income-schedules/detection/accept", contracts);
        Assert.Contains("api/income-schedules/detection/dismiss", contracts);
        Assert.Contains("contracts-income", contracts);
        Assert.Contains("loadIncome(false)", contracts);
    }

    /// <summary>
    /// Income is its own panel, not a row in the contract list.
    ///
    /// Every consumer of that list treats a contract as money that LEAVES — the cashflow's "what is
    /// available" subtracts it, and so does the reconciliation report. A salary carried in there, as a
    /// negative of a negative, is how a wage ends up counted as a fixed cost. The separation is the
    /// point, so it is pinned: the income panel is its own element and the accept path writes an income
    /// schedule, never a contract.
    /// </summary>
    [Fact]
    public void Income_is_a_separate_panel_and_never_becomes_a_contract()
    {
        var contracts = Read("features/contracts.js");

        var incomePanel = contracts.IndexOf("id=\"contracts-income\"", StringComparison.Ordinal);
        Assert.True(incomePanel > 0, "the income panel must exist as its own element");

        // The accept handler for income must not route through the contract endpoints.
        var acceptIncome = Slice(contracts, "async function acceptIncome(", "async function dismissIncome(");
        Assert.Contains("api/income-schedules/detection/accept", acceptIncome);
        Assert.DoesNotContain("api/contracts", acceptIncome);
    }

    /// <summary>
    /// A schedule without an expected amount is a real case — a variable income — and it must say so
    /// rather than print a 0,00 € nobody entered.
    /// </summary>
    [Fact]
    public void A_variable_income_states_that_instead_of_showing_zero()
    {
        var contracts = Read("features/contracts.js");

        Assert.Contains("schedule.expectedAmount == null", contracts);
        Assert.Contains("Betrag schwankt", contracts);
    }

    /// <summary>
    /// Dismissing carries the cadence, because the backend suppresses on account + counterparty +
    /// currency + cycle. Drop the cycle and rejecting a monthly salary also hides the yearly bonus from
    /// the same employer.
    /// </summary>
    [Fact]
    public void Dismissing_one_cadence_does_not_hide_the_others()
    {
        var dismiss = Slice(
            Read("features/contracts.js"),
            "async function dismissIncome(",
            "async function acceptCandidate(");

        Assert.Contains("accountId: candidate.accountId", dismiss);
        Assert.Contains("counterparty: candidate.counterparty", dismiss);
        Assert.Contains("currency: candidate.currency", dismiss);
        Assert.Contains("cycle: candidate.cycle", dismiss);
    }

    private static string Slice(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find '{from}'");
        var end = source.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private string Read(string path)
    {
        using var client = factory.CreateClient();
        var response = client.GetAsync("/" + path).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}
