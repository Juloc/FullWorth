using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// Account identity is (space, provider, hash), so the same IBAN reached through two providers — an ING
/// Girokonto over FinTS for the depots AND over Enable Banking for the bookings — becomes two accounts with
/// two transaction sets, and both used to count in net worth.
///
/// Both connections are kept on purpose: each brings data the other does not. The money is counted once.
/// </summary>
public sealed class DuplicateIbanAccountTests
{
    private const string SharedIban = "DE02120300000000202051";

    [Fact]
    public async Task The_second_provider_reaching_the_same_iban_does_not_count_again()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, scenario.FirstConnection, "enable-banking", "hash-eb", 1000m);
        await IngestAsync(client, scenario, scenario.SecondConnection, "fints", "hash-fints", 1000m);

        var dashboard = await GetAsync(client, scenario, $"/api/analytics/dashboard?fullWorthSpaceId={scenario.Space}");

        // 1 000, not 2 000: the same money reported twice.
        Assert.Equal(1000m, dashboard.GetProperty("accounts").GetDecimal());
    }

    [Fact]
    public async Task Both_accounts_stay_visible_and_the_excluded_one_names_its_twin()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, scenario.FirstConnection, "enable-banking", "hash-eb", 1000m);
        await IngestAsync(client, scenario, scenario.SecondConnection, "fints", "hash-fints", 1000m);

        var accounts = await GetAsync(client, scenario, "/api/accounts?fullWorthSpaceId=" + scenario.Space);
        var rows = accounts.EnumerateArray().ToArray();

        Assert.Equal(2, rows.Length);
        var counted = rows.Single(x => x.GetProperty("includeInNetWorth").GetBoolean());
        var duplicate = rows.Single(x => !x.GetProperty("includeInNetWorth").GetBoolean());
        Assert.Equal(
            counted.GetProperty("id").GetString(),
            duplicate.GetProperty("duplicateOfAccountId").GetString());
        Assert.Equal(
            counted.GetProperty("displayName").GetString(),
            duplicate.GetProperty("duplicateOfDisplayName").GetString());
    }

    [Fact]
    public async Task A_different_iban_is_not_a_duplicate()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, scenario.FirstConnection, "enable-banking", "hash-eb", 1000m);
        await IngestAsync(
            client, scenario, scenario.SecondConnection, "fints", "hash-fints", 250m,
            iban: "DE02500105170137075030");

        var dashboard = await GetAsync(client, scenario, $"/api/analytics/dashboard?fullWorthSpaceId={scenario.Space}");

        Assert.Equal(1250m, dashboard.GetProperty("accounts").GetDecimal());
    }

    // The exclusion happens once, at creation. An account the user deliberately switched back on must not be
    // silently switched off again by the next sync.
    [Fact]
    public async Task Switching_the_duplicate_back_on_survives_the_next_sync()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        await IngestAsync(client, scenario, scenario.FirstConnection, "enable-banking", "hash-eb", 1000m);
        await IngestAsync(client, scenario, scenario.SecondConnection, "fints", "hash-fints", 1000m);

        var accounts = await GetAsync(client, scenario, "/api/accounts?fullWorthSpaceId=" + scenario.Space);
        var duplicateId = accounts.EnumerateArray()
            .Single(x => !x.GetProperty("includeInNetWorth").GetBoolean())
            .GetProperty("id").GetString();

        using var patch = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/accounts/{duplicateId}?fullWorthSpaceId={scenario.Space}")
        {
            Content = JsonContent.Create(new
            {
                displayName = (string?)null,
                isActive = (bool?)null,
                includeInNetWorth = true,
                sortOrder = (int?)null
            })
        };
        patch.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        patch.Headers.Add("X-FullWorth-User-Id", scenario.Owner.ToString("D"));
        (await client.SendAsync(patch)).EnsureSuccessStatusCode();

        await IngestAsync(client, scenario, scenario.SecondConnection, "fints", "hash-fints", 1000m);

        var after = await GetAsync(client, scenario, "/api/accounts?fullWorthSpaceId=" + scenario.Space);
        Assert.All(
            after.EnumerateArray().ToArray(),
            row => Assert.True(row.GetProperty("includeInNetWorth").GetBoolean()));
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid FirstConnection, Guid SecondConnection);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = scenario.Owner,
                EmailNormalized = $"{scenario.Owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Duplicate owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = scenario.Space,
                Name = "Duplicates",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = scenario.Space,
                UserId = scenario.Owner,
                Role = FullWorthSpaceRoles.Owner
            });
            foreach (var (id, provider) in new[]
                     { (scenario.FirstConnection, "enable-banking"), (scenario.SecondConnection, "fints") })
                db.BankConnections.Add(new BankConnection
                {
                    Id = id,
                    FullWorthSpaceId = scenario.Space,
                    Provider = provider,
                    InstitutionName = "ING",
                    Country = "DE",
                    ProviderSessionId = $"session-{id:N}",
                    // The ingest only assigns an owner to a new account when the connection names the
                    // authorizing user, exactly as the real connect flow does.
                    AuthorizationUserId = scenario.Owner
                });
            await db.SaveChangesAsync();
        });
        return scenario;
    }

    private static async Task IngestAsync(
        HttpClient client,
        Scenario scenario,
        Guid connectionId,
        string provider,
        string hash,
        decimal balance,
        string iban = SharedIban)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/banking/ingest")
        {
            Content = JsonContent.Create(new
            {
                connection = new
                {
                    connectionId,
                    provider,
                    institutionName = "ING",
                    country = "DE",
                    providerSessionId = $"session-{connectionId:N}",
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
                        identificationHash = hash,
                        providerAccountId = hash,
                        institutionName = "ING",
                        displayName = $"ING {provider}",
                        product = (string?)null,
                        accountType = "checking",
                        currency = "EUR",
                        ibanLast4 = iban[^4..],
                        isActive = true,
                        iban
                    }
                },
                balances = new[]
                {
                    new
                    {
                        identificationHash = hash,
                        amount = balance,
                        currency = "EUR",
                        balanceType = "closingBooked",
                        referenceDate = (DateOnly?)null,
                        capturedAt = DateTimeOffset.UtcNow
                    }
                },
                transactions = Array.Empty<object>()
            })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        (await client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Scenario scenario, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", scenario.Owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
