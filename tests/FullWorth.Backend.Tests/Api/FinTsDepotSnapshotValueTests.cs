using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// A depot position is only worth something in a valuation if a price row exists for it. The bank does not
/// always send a unit price — it does send the position's market value — and that value used to be dropped,
/// so a depot the bank valued at 40,000 was worth 0 everywhere.
/// </summary>
public sealed class FinTsDepotSnapshotValueTests
{
    [Fact]
    public async Task A_position_without_a_unit_price_is_valued_from_its_reported_market_value()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await SeedConnectionAsync(factory, owner, connectionId);

        var accepted = await PostSnapshotAsync(client, connectionId, today, new
        {
            providerKey = "pos-1",
            name = "World ETF",
            isin = "IE00B4L5Y983",
            wkn = (string?)null,
            currency = "EUR",
            quantity = 10m,
            price = (decimal?)null,
            priceDate = (DateOnly?)null,
            marketValue = 40000m,
            exchange = (string?)null
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var contribution = await ContributionAsync(factory, owner, today);

        Assert.Equal(40000m, contribution.Amount);
        Assert.False(contribution.Incomplete);
    }

    [Fact]
    public async Task A_reported_unit_price_still_wins_over_the_derived_one()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await SeedConnectionAsync(factory, owner, connectionId);

        // Market value and unit price disagree (a stale market value from the bank). The unit price is the
        // more precise figure and must not be overwritten by a derivation.
        var posted = await PostSnapshotAsync(client, connectionId, today, new
        {
            providerKey = "pos-1",
            name = "World ETF",
            isin = "IE00B4L5Y983",
            wkn = (string?)null,
            currency = "EUR",
            quantity = 10m,
            price = (decimal?)100m,
            priceDate = (DateOnly?)null,
            marketValue = (decimal?)999m,
            exchange = (string?)null
        });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var contribution = await ContributionAsync(factory, owner, today);

        Assert.Equal(1000m, contribution.Amount);
    }

    [Fact]
    public async Task A_position_with_neither_price_nor_market_value_is_incomplete_not_zero()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await SeedConnectionAsync(factory, owner, connectionId);

        var posted = await PostSnapshotAsync(client, connectionId, today, new
        {
            providerKey = "pos-1",
            name = "Unpriceable",
            isin = (string?)null,
            wkn = (string?)null,
            currency = "EUR",
            quantity = 5m,
            price = (decimal?)null,
            priceDate = (DateOnly?)null,
            marketValue = (decimal?)null,
            exchange = (string?)null
        });
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        var contribution = await ContributionAsync(factory, owner, today);

        Assert.Equal(0m, contribution.Amount);
        // Nothing is known about this position, and the answer says so rather than claiming a value.
        Assert.True(contribution.Incomplete);
    }

    private static async Task SeedConnectionAsync(
        BackendWebApplicationFactory factory, Guid owner, Guid connectionId) =>
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EXAMPLE.COM",
                DisplayName = "Depot owner",
                IsActive = true
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = connectionId,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "fints",
                InstitutionName = "ING",
                Country = "DE",
                ProviderSessionId = $"fints-{connectionId:N}"
            });
            await db.SaveChangesAsync();
        });

    private static async Task<HttpResponseMessage> PostSnapshotAsync(
        HttpClient client, Guid connectionId, DateOnly asOf, object holding)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/internal/banking/fints/investment-snapshot")
        {
            Content = JsonContent.Create(new
            {
                connectionId,
                depotKey = "depot-1",
                name = "ING Direkt-Depot",
                currency = "EUR",
                asOf,
                holdings = new[] { holding }
            })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// Der Beitrag der Depots zum Nettovermoegen, direkt aus <see cref="InvestmentNetWorthService"/>.
    ///
    /// Bis #177 ging das ueber <c>GET /api/investments/net-worth-contribution</c>. Diese Route war das
    /// FENSTER dieser Tests und sonst nichts: kein Aufrufer im Frontend, und in der Anwendung liest
    /// den Dienst laengst jemand anderes - die Vermoegensuebersicht, die Schnappschuesse und
    /// Analytics. Sie ist deshalb geloescht, und diese Tests fragen den Dienst jetzt direkt.
    ///
    /// Das ist ausserdem ehrlicher benannt: die Tests hier heissen nach einer RECHNUNG ("ohne
    /// Stueckpreis aus dem gemeldeten Marktwert", "weder noch heisst unvollstaendig, nicht null"),
    /// nicht nach einer Adresse. Ueber HTTP zu gehen hat daran nie etwas geprueft, was der direkte
    /// Aufruf nicht auch prueft.
    /// </summary>
    private static async Task<InvestmentNetWorthContribution> ContributionAsync(
        BackendWebApplicationFactory factory, Guid owner, DateOnly asOf)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<InvestmentNetWorthService>();
        return await service.CalculateAsync(FullWorthSpaceDefaults.LegacyId, owner, asOf, CancellationToken.None);
    }
}
