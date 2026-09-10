using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Data;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Ingestion;

/// <summary>
/// <c>IngestionService.InsertBalancesAsync</c> is the only production path that gives a synced or imported
/// account a balance — everything the user sees as "what is in my account" starts here — and it had no test
/// at all in any of the four test projects.
/// </summary>
public sealed class BalanceIngestionTests
{
    [Fact]
    public async Task Every_wallet_of_a_multi_currency_account_is_stored()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var captured = DateTimeOffset.UtcNow;

        var response = await IngestAsync(client, scenario, [
            Balance(scenario.Hash, 100m, "EUR", captured),
            Balance(scenario.Hash, 55m, "USD", captured),
            Balance(scenario.Hash, 2_000_000m, "IDR", captured)
        ]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await ReadBalancesAsync(factory, scenario);

        Assert.Equal(3, stored.Count);
        Assert.Equal(100m, stored["EUR"]);
        Assert.Equal(55m, stored["USD"]);
        Assert.Equal(2_000_000m, stored["IDR"]);
    }

    // The balance's own currency, never the one the account declares: pairing an amount with the account
    // currency converts a foreign wallet at the wrong rate.
    [Fact]
    public async Task A_balance_keeps_the_currency_it_was_reported_in()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, [Balance(scenario.Hash, 77m, "GBP", DateTimeOffset.UtcNow)]);

        var stored = await ReadBalancesAsync(factory, scenario);
        var only = Assert.Single(stored);
        Assert.Equal("GBP", only.Key);
        Assert.Equal(77m, only.Value);
    }

    [Fact]
    public async Task A_later_sync_adds_history_instead_of_overwriting_the_earlier_balance()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var first = DateTimeOffset.UtcNow.AddDays(-1);
        var second = DateTimeOffset.UtcNow;

        await IngestAsync(client, scenario, [Balance(scenario.Hash, 100m, "EUR", first)]);
        await IngestAsync(client, scenario, [Balance(scenario.Hash, 120m, "EUR", second)]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var rows = await db.BalanceSnapshots.AsNoTracking()
            .Where(balance => balance.Currency == "EUR")
            .OrderBy(balance => balance.CapturedAt)
            .Select(balance => balance.Amount)
            .ToListAsync();

        // A historical value stays as of its date; the current balance is the newest one.
        Assert.Equal([100m, 120m], rows);
    }

    [Fact]
    public async Task A_balance_for_an_unknown_account_is_ignored_rather_than_attached_to_someone_else()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var response = await IngestAsync(client, scenario, [
            Balance("hash-that-was-never-announced", 999m, "EUR", DateTimeOffset.UtcNow)
        ]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadBalancesAsync(factory, scenario));
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid Connection, string Hash);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"hash-{Guid.NewGuid():N}");

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = scenario.Owner,
                EmailNormalized = $"{scenario.Owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Ingest owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = scenario.Space,
                Name = "Ingest",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = scenario.Space,
                UserId = scenario.Owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = scenario.Connection,
                FullWorthSpaceId = scenario.Space,
                Provider = "test",
                InstitutionName = "PayPal",
                Country = "DE",
                ProviderSessionId = $"ingest-{scenario.Connection:N}"
            });
            await db.SaveChangesAsync();
        });

        return scenario;
    }

    private static object Balance(string hash, decimal amount, string currency, DateTimeOffset captured) => new
    {
        identificationHash = hash,
        amount,
        currency,
        balanceType = "closingBooked",
        referenceDate = (DateOnly?)null,
        capturedAt = captured
    };

    private static async Task<HttpResponseMessage> IngestAsync(
        HttpClient client, Scenario scenario, object[] balances)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/banking/ingest")
        {
            Content = JsonContent.Create(new
            {
                connection = new
                {
                    connectionId = scenario.Connection,
                    provider = "test",
                    institutionName = "PayPal",
                    country = "DE",
                    providerSessionId = $"ingest-{scenario.Connection:N}",
                    status = "AUTHORIZED",
                    validUntil = (DateTimeOffset?)null,
                    lastSyncedAt = DateTimeOffset.UtcNow,
                    lastError = (string?)null,
                    fullWorthSpaceId = scenario.Space
                },
                accounts = new[]
                {
                    new
                    {
                        identificationHash = scenario.Hash,
                        providerAccountId = scenario.Hash,
                        institutionName = "PayPal",
                        displayName = "PayPal",
                        product = (string?)null,
                        accountType = "wallet",
                        currency = "EUR",
                        ibanLast4 = (string?)null,
                        isActive = true
                    }
                },
                balances,
                transactions = Array.Empty<object>()
            })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        return await client.SendAsync(request);
    }

    private static async Task<Dictionary<string, decimal>> ReadBalancesAsync(
        BackendWebApplicationFactory factory, Scenario scenario)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var accountId = await db.Accounts.AsNoTracking()
            .Where(account => account.IdentificationHash == scenario.Hash)
            .Select(account => account.Id)
            .SingleAsync();
        return await db.BalanceSnapshots.AsNoTracking()
            .Where(balance => balance.AccountId == accountId)
            .ToDictionaryAsync(balance => balance.Currency, balance => balance.Amount);
    }
}
