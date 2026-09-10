using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Pension;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Pension;

/// <summary>
/// The identity and history guarantees of the Altersvorsorge area (docs/PENSION.md): the same policy
/// number at the same provider is one contract, and a new statement adds a snapshot without touching
/// a single value that is already stored.
/// </summary>
public sealed class PensionContractIntegrationTests
{
    /// <summary>
    /// An annual statement for a contract that already exists must never become a second contract.
    /// The create is refused with the id of the contract it belongs to, so the caller adds a snapshot
    /// to it — and a different spelling of the same provider is still the same provider.
    /// </summary>
    [Fact]
    public async Task Same_policy_number_and_provider_never_creates_a_second_contract()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var first = await PostContractAsync(client, scenario, new
        {
            providerName = "Allianz Lebensversicherungs-AG",
            tariffName = "PensionInvest Flex",
            policyNumber = "BAV-2018 / 4711",
            implementationRoute = "direct_insurance",
            currency = "EUR",
            retirementDate = "2050-06-01"
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var created = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var contractId = created.RootElement.GetProperty("id").GetGuid();

        // Same number, written the way a different document prints it, and the provider's legal form
        // spelled differently. Both normalise to the same identity.
        var second = await PostContractAsync(client, scenario, new
        {
            providerName = "Allianz Lebensversicherung AG",
            policyNumber = "bav20184711",
            implementationRoute = "direct_insurance",
            currency = "EUR"
        });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var conflict = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(contractId, conflict.RootElement.GetProperty("existingContractId").GetGuid());

        await factory.SeedAsync(async db =>
            Assert.Equal(1, await db.BavContracts.CountAsync(row => row.FullWorthSpaceId == scenario.Space)));

        // The detection endpoint answers the same question before anything is written, and names the
        // rule that fired so a review screen can explain itself.
        using var match = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/pension/contracts/match?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new { policyNumber = "BAV 2018 4711", providerName = "ALLIANZ LEBENSVERSICHERUNG AG" }));
        Assert.Equal(HttpStatusCode.OK, match.StatusCode);
        using var matched = JsonDocument.Parse(await match.Content.ReadAsStringAsync());
        Assert.True(matched.RootElement.GetProperty("matched").GetBoolean());
        Assert.Equal(contractId, matched.RootElement.GetProperty("contractId").GetGuid());
        Assert.Equal("policy_number", matched.RootElement.GetProperty("matchedOn").GetString());
    }

    /// <summary>
    /// The second statement of a contract adds a row. The first snapshot's numbers are still exactly
    /// what they were, the newest one is the current value, and the asset that carries the value into
    /// net worth follows the newest snapshot rather than the newest insert.
    /// </summary>
    [Fact]
    public async Task A_second_statement_adds_a_snapshot_and_leaves_the_earlier_values_intact()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Pensionskasse Muster",
            policyNumber = "PK-1",
            implementationRoute = "pension_fund",
            currency = "EUR"
        });

        var older = await PostAsync(client, scenario, $"/api/pension/contracts/{contractId}/snapshots", new
        {
            effectiveDate = "2025-01-01",
            balance = 12_500.55m,
            guaranteedBalance = 9_000m,
            source = "document",
            documentSha256 = new string('a', 64)
        });
        Assert.Equal(HttpStatusCode.OK, older.StatusCode);

        var newer = await PostAsync(client, scenario, $"/api/pension/contracts/{contractId}/snapshots", new
        {
            effectiveDate = "2026-01-01",
            balance = 15_200.10m,
            guaranteedBalance = 10_100m,
            source = "document",
            documentSha256 = new string('b', 64)
        });
        Assert.Equal(HttpStatusCode.OK, newer.StatusCode);

        using var history = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/pension/contracts/{contractId}/snapshots?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var json = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        var rows = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);

        var old = rows.Single(row => row.GetProperty("effectiveDate").GetString() == "2025-01-01");
        Assert.Equal(12_500.55m, old.GetProperty("balance").GetDecimal());
        Assert.Equal(9_000m, old.GetProperty("guaranteedBalance").GetDecimal());
        Assert.False(old.GetProperty("isCurrent").GetBoolean());

        var current = rows.Single(row => row.GetProperty("isCurrent").GetBoolean());
        Assert.Equal("2026-01-01", current.GetProperty("effectiveDate").GetString());
        Assert.Equal(15_200.10m, current.GetProperty("balance").GetDecimal());

        // Re-reading the same document produces nothing, and a hand-entered snapshot for a date that
        // already carries one is a conflict rather than an overwrite.
        var repeat = await PostAsync(client, scenario, $"/api/pension/contracts/{contractId}/snapshots", new
        {
            effectiveDate = "2026-01-01",
            balance = 99_999m,
            source = "document",
            documentSha256 = new string('b', 64)
        });
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);

        await factory.SeedAsync(async db =>
        {
            Assert.Equal(2, await db.BavSnapshots.CountAsync(row => row.BavContractId == contractId));
            Assert.Equal(12_500.55m, await db.BavSnapshots
                .Where(row => row.BavContractId == contractId && row.EffectiveDate == new DateOnly(2025, 1, 1))
                .Select(row => row.Balance!.Value).SingleAsync());

            // The value reaches net worth through the existing Asset, as of the snapshot's own date.
            var contract = await db.BavContracts.AsNoTracking().SingleAsync(row => row.Id == contractId);
            var asset = await db.Assets.AsNoTracking().SingleAsync(row => row.Id == contract.AssetId!.Value);
            Assert.Equal(AssetKinds.InsurancePension, asset.Kind);
            Assert.Equal(15_200.10m, asset.CurrentValue);
            Assert.Equal("EUR", asset.Currency);
            Assert.Equal(new DateOnly(2026, 1, 1), asset.ValuedAt);
        });
    }

    /// <summary>
    /// Entering last year's statement after this year's must not move the contract's value backwards:
    /// "current" is the newest effective date, not the newest insert.
    /// </summary>
    [Fact]
    public async Task An_older_statement_entered_afterwards_does_not_become_the_current_value()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Unterstützungskasse Nord",
            policyNumber = "UK-77",
            implementationRoute = "provident_fund",
            currency = "EUR"
        });

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/snapshots",
            new { effectiveDate = "2026-01-01", balance = 20_000m })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, scenario,
            $"/api/pension/contracts/{contractId}/snapshots",
            new { effectiveDate = "2024-01-01", balance = 8_000m })).StatusCode);

        await factory.SeedAsync(async db =>
        {
            var current = await db.BavSnapshots.AsNoTracking()
                .SingleAsync(row => row.BavContractId == contractId && row.IsCurrent);
            Assert.Equal(new DateOnly(2026, 1, 1), current.EffectiveDate);

            var contract = await db.BavContracts.AsNoTracking().SingleAsync(row => row.Id == contractId);
            var asset = await db.Assets.AsNoTracking().SingleAsync(row => row.Id == contract.AssetId!.Value);
            Assert.Equal(20_000m, asset.CurrentValue);
        });
    }

    /// <summary>
    /// Reads need space membership, writes need the owner role, and someone outside the space is told
    /// nothing at all — the same ordering the rest of the space-scoped modules use.
    /// </summary>
    [Fact]
    public async Task A_member_can_read_but_not_write_and_an_outsider_gets_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory);
        using var client = factory.CreateClient();

        var contractId = await CreateContractAsync(client, scenario, new
        {
            providerName = "Provider One",
            policyNumber = "P-1",
            currency = "EUR"
        });

        using var memberRead = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/pension/contracts?fullWorthSpaceId={scenario.Space}", scenario.Member));
        Assert.Equal(HttpStatusCode.OK, memberRead.StatusCode);

        using var memberWrite = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/pension/contracts/{contractId}/snapshots?fullWorthSpaceId={scenario.Space}", scenario.Member,
            new { effectiveDate = "2026-01-01", balance = 1m }));
        Assert.Equal(HttpStatusCode.Forbidden, memberWrite.StatusCode);

        using var outsideRead = await client.SendAsync(Request(HttpMethod.Get,
            $"/api/pension/contracts/{contractId}?fullWorthSpaceId={scenario.Space}", scenario.Outside));
        Assert.Equal(HttpStatusCode.NotFound, outsideRead.StatusCode);

        using var outsideWrite = await client.SendAsync(Request(HttpMethod.Post,
            $"/api/pension/contracts/{contractId}/snapshots?fullWorthSpaceId={scenario.Space}", scenario.Outside,
            new { effectiveDate = "2026-01-01", balance = 1m }));
        Assert.Equal(HttpStatusCode.NotFound, outsideWrite.StatusCode);
    }

    // ---- helpers ----

    internal sealed record Scenario(Guid Space, Guid Owner, Guid Member, Guid Outside);

    internal static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Member, scenario.Outside })
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                    DisplayName = $"Pension {userId:N}",
                    IsActive = true
                });

            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = scenario.Space,
                Name = "Pension space",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Member, Role = FullWorthSpaceRoles.Member });
            await db.SaveChangesAsync();
        });
        return scenario;
    }

    internal static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    internal static Task<HttpResponseMessage> PostContractAsync(
        HttpClient client, Scenario scenario, object body) =>
        client.SendAsync(Request(HttpMethod.Post,
            $"/api/pension/contracts?fullWorthSpaceId={scenario.Space}", scenario.Owner, body));

    internal static Task<HttpResponseMessage> PostAsync(
        HttpClient client, Scenario scenario, string path, object body) =>
        client.SendAsync(Request(HttpMethod.Post,
            $"{path}?fullWorthSpaceId={scenario.Space}", scenario.Owner, body));

    internal static async Task<Guid> CreateContractAsync(HttpClient client, Scenario scenario, object body)
    {
        using var response = await PostContractAsync(client, scenario, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetGuid();
    }
}
