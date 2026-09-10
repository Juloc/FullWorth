using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

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

        var contribution = await ContributionAsync(client, owner, today);

        Assert.Equal(40000m, contribution.GetProperty("total").GetDecimal());
        Assert.False(contribution.GetProperty("incomplete").GetBoolean());
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

        var contribution = await ContributionAsync(client, owner, today);

        Assert.Equal(1000m, contribution.GetProperty("total").GetDecimal());
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

        var contribution = await ContributionAsync(client, owner, today);

        Assert.Equal(0m, contribution.GetProperty("total").GetDecimal());
        // Nothing is known about this position, and the answer says so rather than claiming a value.
        Assert.True(contribution.GetProperty("incomplete").GetBoolean());
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

    private static async Task<JsonElement> ContributionAsync(HttpClient client, Guid owner, DateOnly asOf)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/investments/net-worth-contribution-v2?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&asOf={asOf:yyyy-MM-dd}");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
