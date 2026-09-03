using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class AssetValuationIntegrationTests
{
    [Fact]
    public async Task AcceptedValuationUpdatesAssetAndKeepsHistoryTransactionally()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/assets/{scenario.Asset}/valuations?fullWorthSpaceId={scenario.Space}",
            scenario.Owner,
            new
            {
                amount = 125_000m,
                currency = "eur",
                valuedAt = "2026-09-01",
                method = "manual",
                lowEstimate = 120_000m,
                highEstimate = 130_000m,
                confidence = 0.9m,
                isAccepted = true
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var createdJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(125_000m, createdJson.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", createdJson.RootElement.GetProperty("currency").GetString());
        Assert.True(createdJson.RootElement.GetProperty("isCurrent").GetBoolean());
        Assert.True(createdJson.RootElement.GetProperty("isAccepted").GetBoolean());
        Assert.Equal(scenario.Owner, createdJson.RootElement.GetProperty("createdByUserId").GetGuid());

        using var historyResponse = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/assets/{scenario.Asset}/valuations?fullWorthSpaceId={scenario.Space}",
            scenario.Member));
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        using var historyJson = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
        var history = historyJson.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, history.Length);
        Assert.Single(history.Where(row => row.GetProperty("isCurrent").GetBoolean()));
        Assert.Equal(125_000m, history.Single(row => row.GetProperty("isCurrent").GetBoolean()).GetProperty("amount").GetDecimal());

        await factory.SeedAsync(async db =>
        {
            var asset = await db.Assets.SingleAsync(x => x.Id == scenario.Asset);
            Assert.Equal(125_000m, asset.CurrentValue);
            Assert.Equal("EUR", asset.Currency);
            Assert.Equal(new DateOnly(2026, 9, 1), asset.ValuedAt);
            Assert.True(await db.AuditEvents.AnyAsync(x =>
                x.FullWorthSpaceId == scenario.Space &&
                x.ActorUserId == scenario.Owner &&
                x.Action == "asset.valuation.accepted"));
        });
    }

    [Fact]
    public async Task DraftValuationDoesNotChangeCurrentAssetValue()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/assets/{scenario.Asset}/valuations?fullWorthSpaceId={scenario.Space}",
            scenario.Owner,
            new
            {
                amount = 140_000m,
                currency = "EUR",
                valuedAt = "2026-09-01",
                method = "internal_estimate",
                lowEstimate = 130_000m,
                highEstimate = 150_000m,
                confidence = 0.6m,
                isAccepted = false
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("isCurrent").GetBoolean());
        Assert.False(json.RootElement.GetProperty("isAccepted").GetBoolean());

        await factory.SeedAsync(async db =>
        {
            var asset = await db.Assets.SingleAsync(x => x.Id == scenario.Asset);
            Assert.Equal(100_000m, asset.CurrentValue);
            Assert.Equal(new DateOnly(2026, 8, 1), asset.ValuedAt);
        });
    }

    [Fact]
    public async Task MemberCanReadValuationsButCannotCreateAndOutsideUserGetsNotFound()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var memberRead = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/assets/{scenario.Asset}/valuations?fullWorthSpaceId={scenario.Space}",
            scenario.Member));
        Assert.Equal(HttpStatusCode.OK, memberRead.StatusCode);

        using var memberWrite = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/assets/{scenario.Asset}/valuations?fullWorthSpaceId={scenario.Space}",
            scenario.Member,
            new { amount = 101_000m, currency = "EUR", method = "manual" }));
        Assert.Equal(HttpStatusCode.Forbidden, memberWrite.StatusCode);

        using var outsideRead = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/assets/{scenario.Asset}/valuations?fullWorthSpaceId={scenario.Space}",
            scenario.Outside));
        Assert.Equal(HttpStatusCode.NotFound, outsideRead.StatusCode);
    }

    [Fact]
    public async Task ExistingAssetEndpointsNormalizeKindsAndAutomaticallyCreateValuationHistory()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var create = await client.SendAsync(UserRequest(
            HttpMethod.Post,
            $"/api/assets?fullWorthSpaceId={scenario.Space}",
            scenario.Owner,
            AssetPayload("Compatibility asset", 75m)));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        using var createJson = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var assetId = createJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("other", createJson.RootElement.GetProperty("kind").GetString());

        using var initialHistory = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/assets/{assetId}/valuations?fullWorthSpaceId={scenario.Space}",
            scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, initialHistory.StatusCode);
        using var initialJson = JsonDocument.Parse(await initialHistory.Content.ReadAsStringAsync());
        var initialRows = initialJson.RootElement.EnumerateArray().ToArray();
        Assert.Single(initialRows);
        Assert.Equal(75m, initialRows[0].GetProperty("amount").GetDecimal());
        Assert.Equal("manual", initialRows[0].GetProperty("method").GetString());
        Assert.True(initialRows[0].GetProperty("isCurrent").GetBoolean());

        using var update = await client.SendAsync(UserRequest(
            HttpMethod.Put,
            $"/api/assets/{assetId}?fullWorthSpaceId={scenario.Space}",
            scenario.Owner,
            AssetPayload("Compatibility asset", 80m)));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using var history = await client.SendAsync(UserRequest(
            HttpMethod.Get,
            $"/api/assets/{assetId}/valuations?fullWorthSpaceId={scenario.Space}",
            scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        var rows = historyJson.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Single(rows.Where(row => row.GetProperty("isCurrent").GetBoolean()));
        Assert.Equal(80m, rows.Single(row => row.GetProperty("isCurrent").GetBoolean()).GetProperty("amount").GetDecimal());
    }

    private static object AssetPayload(string name, decimal value) => new
    {
        name,
        kind = "cash",
        currentValue = value,
        currency = "eur",
        valuedAt = "2026-09-01",
        annualGrowthRate = (decimal?)null,
        includeInNetWorth = true,
        notes = "valuation compatibility test"
    };

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<Scenario> SeedScenarioAsync(BackendWebApplicationFactory factory)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            foreach (var userId in new[] { scenario.Owner, scenario.Member, scenario.Outside })
            {
                db.Users.Add(new FullWorthUser
                {
                    Id = userId,
                    EmailNormalized = $"{userId:N}@EXAMPLE.COM",
                    DisplayName = $"Valuation {userId:N}",
                    IsActive = true
                });
            }

            db.FullWorthSpaces.Add(new FullWorthSpace
            {
                Id = scenario.Space,
                Name = "Valuation space",
                BaseCurrency = "EUR"
            });
            db.FullWorthSpaceMembers.AddRange(
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Owner, Role = FullWorthSpaceRoles.Owner },
                new FullWorthSpaceMember { FullWorthSpaceId = scenario.Space, UserId = scenario.Member, Role = FullWorthSpaceRoles.Member });
            db.Assets.Add(new Asset
            {
                Id = scenario.Asset,
                FullWorthSpaceId = scenario.Space,
                Name = "Property seed",
                Kind = "real_estate",
                CurrentValue = 100_000m,
                Currency = "EUR",
                ValuedAt = new DateOnly(2026, 8, 1),
                IncludeInNetWorth = true
            });
            await db.SaveChangesAsync();
        });
        return scenario;
    }

    private sealed record Scenario(Guid Owner, Guid Member, Guid Outside, Guid Space, Guid Asset);
}
