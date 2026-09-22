using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Loans;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// Eine Zahl, drei Rechnungen (#174).
///
/// Das Issue verlangt als erste Regel: "Alle Bereiche sollen dieselbe zentrale Berechnungsquelle
/// verwenden, insbesondere Dashboard, Vermögen, Statistiken und historische Charts."
///
/// Es gibt drei Stellen, die das Nettovermoegen ausrechnen - <c>AnalyticsService.DashboardForUserAsync</c>,
/// <c>WealthOverviewService</c> und <c>NetWorthSnapshotService</c> - und aus genau dieser Doppelung ist
/// schon ein Fehler entstanden: das Dashboard las nur <c>Liabilities</c> und nicht <c>Loans</c>, war also
/// um jede Hypothek zu hoch, waehrend der Verlauf daneben richtig rechnete. Der Kommentar dazu steht
/// heute noch im Code.
///
/// Dieser Test vergleicht die beiden Zahlen, die ein Benutzer im selben Moment nebeneinander sehen
/// kann. Sie muessen gleich sein - egal, wie viele Rechnungen es dahinter gibt.
/// </summary>
public sealed class NetWorthSingleSourceTests
{
    [Fact]
    public async Task The_dashboard_and_the_wealth_page_show_the_same_net_worth()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var dashboard = await GetAsync(client, world, "/api/analytics/dashboard");
        var wealth = await GetAsync(client, world, "/api/wealth/overview");

        var fromDashboard = dashboard.GetProperty("netWorth").GetDecimal();
        var fromWealth = wealth.GetProperty("netWorth").GetDecimal();

        Assert.Equal(fromWealth, fromDashboard);
    }

    /// <summary>
    /// Und zwar auch dann, wenn ein Darlehen dabei ist - das war der Fall, an dem die beiden Rechnungen
    /// schon einmal auseinanderliefen.
    /// </summary>
    [Fact]
    public async Task A_loan_reduces_both_figures_by_the_same_amount()
    {
        using var factory = new BackendWebApplicationFactory();
        var world = await SeedAsync(factory, loanBalance: 250_000m);
        using var client = factory.CreateClient();

        var dashboard = await GetAsync(client, world, "/api/analytics/dashboard");
        var wealth = await GetAsync(client, world, "/api/wealth/overview");

        Assert.Equal(
            wealth.GetProperty("netWorth").GetDecimal(),
            dashboard.GetProperty("netWorth").GetDecimal());
        // Und die Zahl ist nicht zufaellig gleich, weil beide das Darlehen vergessen haetten.
        Assert.True(dashboard.GetProperty("liabilities").GetDecimal() >= 250_000m,
            "Das Darlehen fehlt in den Verbindlichkeiten des Dashboards.");
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, World world, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{path}?fullWorthSpaceId={world.Space:D}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", world.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private sealed record World(Guid Space, Guid Owner);

    private static async Task<World> SeedAsync(BackendWebApplicationFactory factory, decimal loanBalance = 0m)
    {
        var world = new World(Guid.NewGuid(), Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var accountId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = world.Owner,
                EmailNormalized = $"{world.Owner:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = world.Space, Name = "Vermoegen", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = world.Space,
                UserId = world.Owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = connectionId,
                FullWorthSpaceId = world.Space,
                Provider = "manual",
                InstitutionName = "Testbank",
                Status = "authorized"
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = world.Space,
                BankConnectionId = connectionId,
                IdentificationHash = $"h-{accountId:N}",
                ProviderAccountId = $"a-{accountId:N}",
                InstitutionName = "Testbank",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = world.Owner,
                OwnershipType = AccountOwnershipTypes.Owner
            });
            db.BalanceSnapshots.Add(new BalanceSnapshot
            {
                AccountId = accountId,
                Amount = 12_500m,
                Currency = "EUR",
                BalanceType = "closingBooked",
                Source = BalanceSources.Manual,
                ReferenceDate = DateOnly.FromDateTime(DateTime.UtcNow.Date),
                CapturedAt = DateTimeOffset.UtcNow
            });
            db.Assets.Add(new Asset
            {
                FullWorthSpaceId = world.Space,
                Name = "Eigentumswohnung",
                Kind = "real_estate",
                CurrentValue = 420_000m,
                Currency = "EUR",
                IncludeInNetWorth = true
            });
            db.Liabilities.Add(new Liability
            {
                FullWorthSpaceId = world.Space,
                Name = "Ratenkredit",
                Kind = "loan",
                CurrentBalance = 4_000m,
                Currency = "EUR",
                IncludeInNetWorth = true
            });
            if (loanBalance > 0m)
                db.Loans.Add(new Loan
                {
                    FullWorthSpaceId = world.Space,
                    Name = "Immobiliendarlehen",
                    OriginalPrincipal = 300_000m,
                    PaymentAmount = 1_200m,
                    NominalInterestRate = 3.5m,
                    StartDate = new DateOnly(2020, 1, 1),
                    CurrentBalance = loanBalance,
                    Currency = "EUR",
                    IsActive = true
                });
            await db.SaveChangesAsync();
        });
        return world;
    }
}
