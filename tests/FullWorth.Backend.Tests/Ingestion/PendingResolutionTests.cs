using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Ingestion;

/// <summary>
/// A pending row is an authorisation, not a ledger entry. Its key includes the status, so when the same
/// payment returns as BOOK it arrives under a different key and is inserted as a SECOND row — and nothing
/// ever removed the pending one. Every pending payment therefore ended up as two rows, counted twice
/// wherever pending is included, and a cancelled authorisation stayed forever.
/// </summary>
public sealed class PendingResolutionTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task A_pending_row_the_provider_no_longer_reports_is_resolved()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        // First sync: one pending authorisation.
        await IngestAsync(client, scenario, [Pending("fp:auth-1", -25m)]);
        Assert.Equal(1, await CountAsync(factory, scenario));

        // Next sync: it booked, so it arrives under its booked key and the pending one is gone from the feed.
        await IngestAsync(client, scenario, [Booked("er:booking-1", -25m)], seenPending: []);

        var rows = await RowsAsync(factory, scenario);
        Assert.Equal("er:booking-1", Assert.Single(rows).ExternalKey);
    }

    [Fact]
    public async Task A_pending_row_the_provider_still_reports_is_kept()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, [Pending("fp:auth-1", -25m)]);
        await IngestAsync(client, scenario, [Pending("fp:auth-1", -25m)], seenPending: ["fp:auth-1"]);

        Assert.Equal(1, await CountAsync(factory, scenario));
    }

    // Without a reconciliation the ingest must not touch anything - a partial or page-limited sync sends
    // none, because it cannot tell a booked row from one it simply did not reach.
    [Fact]
    public async Task Without_a_reconciliation_nothing_is_removed()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, [Pending("fp:auth-1", -25m)]);
        await IngestAsync(client, scenario, [Booked("er:booking-1", -25m)], seenPending: null);

        Assert.Equal(2, await CountAsync(factory, scenario));
    }

    // The feed says nothing about rows outside the window it covered.
    [Fact]
    public async Task A_pending_row_older_than_the_window_is_left_alone()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, [Pending("fp:old", -25m, Today.AddDays(-40))]);
        await IngestAsync(client, scenario, [], seenPending: [], windowFrom: Today.AddDays(-7));

        Assert.Equal(1, await CountAsync(factory, scenario));
    }

    // Losing what the user entered is worse than a leftover row.
    [Fact]
    public async Task A_pending_row_the_user_has_touched_is_left_alone()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, [Pending("fp:auth-1", -25m)]);
        await factory.SeedAsync(async db =>
        {
            var pending = await db.Transactions.SingleAsync(x => x.ExternalKey == "fp:auth-1");
            pending.UserNote = "Kaution Werkstatt";
            await db.SaveChangesAsync();
        });

        await IngestAsync(client, scenario, [], seenPending: []);

        var row = Assert.Single(await RowsAsync(factory, scenario));
        Assert.Equal("fp:auth-1", row.ExternalKey);
        Assert.Equal("Kaution Werkstatt", row.UserNote);
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid Connection, string Hash);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"pending-{Guid.NewGuid():N}");
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = scenario.Owner,
                EmailNormalized = $"{scenario.Owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Pending owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = scenario.Space,
                Name = "Pending",
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
                Provider = "enable-banking",
                InstitutionName = "Bank",
                Country = "DE",
                ProviderSessionId = $"pending-{scenario.Connection:N}",
                AuthorizationUserId = scenario.Owner
            });
            await db.SaveChangesAsync();
        });
        return scenario;
    }

    private static object Pending(string key, decimal amount, DateOnly? date = null) =>
        Transaction(key, amount, "PDNG", date);

    private static object Booked(string key, decimal amount, DateOnly? date = null) =>
        Transaction(key, amount, "BOOK", date);

    private static object Transaction(string key, decimal amount, string status, DateOnly? date) => new
    {
        identificationHash = (string?)null,
        externalKey = key,
        providerTransactionId = (string?)null,
        status,
        bookingDate = date ?? Today,
        valueDate = date ?? Today,
        amount,
        currency = "EUR",
        counterparty = "Werkstatt",
        description = (string?)null,
        merchantCategoryCode = (string?)null,
        entryReference = (string?)null,
        rawJson = "{}"
    };

    private static async Task IngestAsync(
        HttpClient client,
        Scenario scenario,
        object[] transactions,
        IReadOnlyList<string>? seenPending = null,
        DateOnly? windowFrom = null,
        bool omitReconciliation = false)
    {
        var withHash = transactions
            .Select(item => (object)Rehash(item, scenario.Hash))
            .ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/banking/ingest")
        {
            Content = JsonContent.Create(new
            {
                connection = new
                {
                    connectionId = scenario.Connection,
                    provider = "enable-banking",
                    institutionName = "Bank",
                    country = "DE",
                    providerSessionId = $"pending-{scenario.Connection:N}",
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
                        institutionName = "Bank",
                        displayName = "Giro",
                        product = (string?)null,
                        accountType = "checking",
                        currency = "EUR",
                        ibanLast4 = (string?)null,
                        isActive = true
                    }
                },
                balances = Array.Empty<object>(),
                transactions = withHash,
                pendingReconciliations = seenPending is null && !omitReconciliation
                    ? null
                    : new[]
                    {
                        new
                        {
                            identificationHash = scenario.Hash,
                            seenExternalKeys = seenPending ?? [],
                            windowFrom
                        }
                    }
            })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static object Rehash(object item, string hash)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(item)!.AsObject();
        json["identificationHash"] = hash;
        return json;
    }

    private static async Task<List<FinanceTransaction>> RowsAsync(
        BackendWebApplicationFactory factory, Scenario scenario)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
        var accountId = await db.Accounts.AsNoTracking()
            .Where(account => account.IdentificationHash == scenario.Hash)
            .Select(account => account.Id)
            .SingleAsync();
        return await db.Transactions.AsNoTracking()
            .Where(transaction => transaction.AccountId == accountId)
            .OrderBy(transaction => transaction.ExternalKey)
            .ToListAsync();
    }

    private static async Task<int> CountAsync(BackendWebApplicationFactory factory, Scenario scenario) =>
        (await RowsAsync(factory, scenario)).Count;
}
