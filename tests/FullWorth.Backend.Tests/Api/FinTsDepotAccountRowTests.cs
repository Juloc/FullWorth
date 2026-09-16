using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// Das Depot steht mit seinem Wert in der Kontenliste - und wird trotzdem nur EINMAL gezaehlt.
///
/// Ein Depot wurde nie ein Konto: der Depotzweig der Synchronisation endete im Depotstand, also in
/// einem <c>InvestmentPortfolio</c> mit <c>AccountId = NULL</c>. Bei den Konten stand es damit gar
/// nicht, nur unter Vermoegen - und nichts im Ablauf sagte das.
///
/// Der Schutz gegen Doppelzaehlung in <c>InvestmentNetWorthService</c> ist genau fuer ein verknuepftes
/// Konto geschrieben ("valuationUsable"), hatte fuer FinTS-Depots aber nie einen Erzeuger. Jetzt legt
/// der Depotzweig das Konto an, der Depotstand verknuepft es und schreibt den Wert als Saldo - und der
/// Schutz laeuft zum ersten Mal wirklich an.
/// </summary>
public sealed class FinTsDepotAccountRowTests
{
    private const string DepotKey = "depot-hash-1";

    [Fact]
    public async Task TheDepotAppearsInTheAccountListWithItsValue()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await SeedConnectionAsync(factory, owner, connectionId);
        await IngestDepotAccountAsync(client, connectionId);
        Assert.Equal(HttpStatusCode.OK, (await PostSnapshotAsync(client, connectionId, today, Holding(40000m))).StatusCode);

        var depot = await DepotRowAsync(client, owner);

        Assert.Equal("ING Direkt-Depot", depot.GetProperty("displayName").GetString());
        Assert.Equal(40000m, depot.GetProperty("latestBalance").GetProperty("amount").GetDecimal());
        // "aufgezeichnet", nicht "gebucht" oder "verfuegbar" - ein Depotwert ist keiner von beiden.
        Assert.Equal("recorded", depot.GetProperty("latestBalance").GetProperty("meaning").GetString());
    }

    /// <summary>
    /// Der Wert zaehlt einmal: als Depot. Das Konto daneben wird ausgenommen, sonst stuende dasselbe
    /// Geld zweimal im Vermoegen.
    /// </summary>
    [Fact]
    public async Task TheValueIsCountedExactlyOnce()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await SeedConnectionAsync(factory, owner, connectionId);
        await IngestDepotAccountAsync(client, connectionId);
        await PostSnapshotAsync(client, connectionId, today, Holding(40000m));

        var dashboard = await JsonAsync(client, owner, $"/api/analytics/dashboard?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&currency=EUR");

        // Das Depot ist ein Investment, kein Kontoguthaben - und der Betrag steht genau einmal da.
        Assert.Equal(0m, dashboard.GetProperty("accounts").GetDecimal());
        Assert.Equal(40000m, dashboard.GetProperty("netWorth").GetDecimal());
    }

    /// <summary>Ein zweiter Abruf legt kein zweites Konto und kein zweites Depot an.</summary>
    [Fact]
    public async Task ASecondSyncCreatesNeitherASecondAccountNorASecondPortfolio()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await SeedConnectionAsync(factory, owner, connectionId);
        for (var run = 0; run < 2; run++)
        {
            await IngestDepotAccountAsync(client, connectionId);
            await PostSnapshotAsync(client, connectionId, today, Holding(40000m));
        }

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.Accounts.CountAsync(x => x.IdentificationHash == DepotKey));

            var portfolios = await db.Database.SqlQuery<Guid?>(
                $"""SELECT "AccountId" AS "Value" FROM "InvestmentPortfolios" WHERE "ProviderName" LIKE 'fints:%'""")
                .ToListAsync();
            var linked = Assert.Single(portfolios);
            Assert.NotNull(linked);
        });
    }

    /// <summary>
    /// Ohne brauchbaren Kurs wird KEIN Saldo geschrieben. Die Zeile zeigt dann nichts - nicht Null.
    /// Eine selbstbewusste Null waere schlimmer als eine Luecke: sie sieht aus wie eine Messung.
    /// </summary>
    [Fact]
    public async Task WithoutAUsableValuationTheRowShowsNoAmountRatherThanZero()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await SeedConnectionAsync(factory, owner, connectionId);
        await IngestDepotAccountAsync(client, connectionId);
        await PostSnapshotAsync(client, connectionId, today, new
        {
            providerKey = "pos-1",
            name = "Unbewertbar",
            isin = (string?)null,
            wkn = (string?)null,
            currency = "EUR",
            quantity = 5m,
            price = (decimal?)null,
            priceDate = (DateOnly?)null,
            marketValue = (decimal?)null,
            exchange = (string?)null
        });

        var depot = await DepotRowAsync(client, owner);

        Assert.True(
            !depot.TryGetProperty("latestBalance", out var balance) || balance.ValueKind == JsonValueKind.Null,
            "Ohne Bewertung darf die Zeile keinen Betrag behaupten.");
    }

    /// <summary>
    /// Eine vom Eigentuemer von Hand gesetzte Verknuepfung ueberlebt jeden weiteren Abruf. Das
    /// <c>AND "AccountId" IS NULL</c> im Verknuepfen ist genau dafuer da.
    /// </summary>
    [Fact]
    public async Task AHandMadeLinkIsNeverOverwritten()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var owner = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var otherAccount = Guid.NewGuid();

        await SeedConnectionAsync(factory, owner, connectionId);
        await IngestDepotAccountAsync(client, connectionId);
        await PostSnapshotAsync(client, connectionId, today, Holding(40000m));

        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FullWorth.Backend.Modules.Accounts.FinanceAccount
            {
                Id = otherAccount,
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                Provider = "manual",
                IdentificationHash = "haendisch",
                ProviderAccountId = "haendisch",
                InstitutionName = "Manuell",
                DisplayName = "Anderes Depot",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync(
                $"""UPDATE "InvestmentPortfolios" SET "AccountId"={otherAccount} WHERE "ProviderName" LIKE 'fints:%'""");
        });

        await PostSnapshotAsync(client, connectionId, today, Holding(41000m));

        await factory.SeedAsync(async db =>
        {
            var linked = await db.Database.SqlQuery<Guid?>(
                $"""SELECT "AccountId" AS "Value" FROM "InvestmentPortfolios" WHERE "ProviderName" LIKE 'fints:%'""")
                .SingleAsync();
            Assert.Equal(otherAccount, linked);
        });
    }

    private static object Holding(decimal marketValue) => new
    {
        providerKey = "pos-1",
        name = "World ETF",
        isin = "IE00B4L5Y983",
        wkn = (string?)null,
        currency = "EUR",
        quantity = 10m,
        price = (decimal?)null,
        priceDate = (DateOnly?)null,
        marketValue = (decimal?)marketValue,
        exchange = (string?)null
    };

    /// <summary>Das Depotkonto, so wie der Depotzweig der Synchronisation es einspielt.</summary>
    private static async Task IngestDepotAccountAsync(HttpClient client, Guid connectionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/banking/ingest")
        {
            Content = JsonContent.Create(new
            {
                connection = new
                {
                    connectionId,
                    provider = "fints",
                    institutionName = "ING",
                    country = "DE",
                    providerSessionId = $"fints-{connectionId:N}",
                    status = "AUTHORIZED",
                    validUntil = (DateTimeOffset?)null,
                    lastSyncedAt = DateTimeOffset.UtcNow,
                    lastError = (string?)null,
                    fullWorthSpaceId = FullWorthSpaceDefaults.LegacyId
                },
                accounts = new[]
                {
                    new
                    {
                        identificationHash = DepotKey,
                        providerAccountId = "fints:" + DepotKey,
                        institutionName = "ING",
                        displayName = "ING Direkt-Depot",
                        product = "ING Direkt-Depot",
                        accountType = "securities",
                        currency = "EUR",
                        ibanLast4 = "7890",
                        isActive = true
                    }
                },
                balances = Array.Empty<object>(),
                transactions = Array.Empty<object>()
            })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> DepotRowAsync(HttpClient client, Guid owner)
    {
        var accounts = await JsonAsync(client, owner, $"/api/accounts?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}");
        var rows = accounts.ValueKind == JsonValueKind.Array
            ? accounts.EnumerateArray()
            : accounts.GetProperty("items").EnumerateArray();
        return Assert.Single(rows
            .Where(row => row.GetProperty("displayName").GetString() == "ING Direkt-Depot")
            .ToArray());
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, Guid owner, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PostSnapshotAsync(
        HttpClient client, Guid connectionId, DateOnly asOf, object holding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/banking/fints/investment-snapshot")
        {
            Content = JsonContent.Create(new
            {
                connectionId,
                depotKey = DepotKey,
                name = "ING Direkt-Depot",
                currency = "EUR",
                asOf,
                holdings = new[] { holding }
            })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        return await client.SendAsync(request);
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
                ProviderSessionId = $"fints-{connectionId:N}",
                // Ohne ihn bekommt das eingespielte Konto keinen Eigentuemer und ist unsichtbar.
                AuthorizationUserId = owner
            });
            await db.SaveChangesAsync();
        });
}
