using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// An asset now keeps two different dates apart: the day somebody stated the value holds for, and the
/// day FullWorth learned the figure. The first is optional and never invented; the second is stamped by
/// the database. Everything a stale-accept rule is allowed to conclude follows from that distinction.
/// </summary>
public sealed class AssetValuationAsOfIntegrationTests
{
    [Fact]
    public async Task An_asset_saved_without_a_date_gets_no_appraisal_date_but_records_when_it_arrived()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, valuedAt: null);
        using var client = factory.CreateClient();

        using var created = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new { name = "Undated house", kind = "real_estate", currentValue = 300_000m, currency = "EUR", valuedAt = (string?)null, annualGrowthRate = (decimal?)null, includeInNetWorth = true, notes = (string?)null }));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        Guid assetId;
        using (var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            assetId = json.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("valuedAt").ValueKind);
            Assert.InRange(
                json.RootElement.GetProperty("valueRecordedAt").GetDateTimeOffset(),
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddMinutes(5));
        }

        // The mirrored history row still needs a date to sort by, but it says the date was not stated.
        var history = await HistoryAsync(client, scenario, assetId);
        var row = Assert.Single(history);
        Assert.False(row.GetProperty("valuedAtIsStated").GetBoolean());
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.Parse(row.GetProperty("valuedAt").GetString()!));
    }

    // The flow a naive "is it older than the current one" check used to reject: the asset was never
    // appraised, so nothing it holds is evidence that a real appraisal from last month is stale.
    [Fact]
    public async Task An_appraisal_older_than_an_undated_value_is_still_accepted()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, valuedAt: null);
        using var client = factory.CreateClient();
        var appraised = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-40);

        using var accept = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{scenario.Asset}/valuations?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new { amount = 250_000m, currency = "EUR", valuedAt = appraised.ToString("yyyy-MM-dd"), method = "appraisal", isAccepted = true }));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);

        await factory.SeedAsync(async db =>
        {
            var asset = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == scenario.Asset);
            Assert.Equal(250_000m, asset.CurrentValue);
            Assert.Equal("EUR", asset.Currency);
            Assert.Equal(appraised, asset.ValuedAt);
        });
    }

    [Fact]
    public async Task Accepting_a_valuation_older_than_the_stated_current_one_is_refused_but_stays_recordable()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, new DateOnly(2026, 8, 1));
        using var client = factory.CreateClient();

        using var refused = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{scenario.Asset}/valuations?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new { amount = 90_000m, currency = "EUR", valuedAt = "2026-07-01", method = "appraisal", isAccepted = true }));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("2026-07-01", body, StringComparison.Ordinal);
        Assert.Contains("2026-08-01", body, StringComparison.Ordinal);
        await AssertSeedValueUnchangedAsync(factory, scenario);

        // Refusing to make it the current value must not refuse the information itself.
        using var recorded = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{scenario.Asset}/valuations?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new { amount = 90_000m, currency = "EUR", valuedAt = "2026-07-01", method = "appraisal", isAccepted = false }));
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        await AssertSeedValueUnchangedAsync(factory, scenario);

        var history = await HistoryAsync(client, scenario, scenario.Asset);
        Assert.Equal(2, history.Length);
        var older = history.Single(x => x.GetProperty("amount").GetDecimal() == 90_000m);
        Assert.False(older.GetProperty("isAccepted").GetBoolean());
        Assert.False(older.GetProperty("isCurrent").GetBoolean());
        Assert.True(older.GetProperty("valuedAtIsStated").GetBoolean());
        Assert.Equal(100_000m, history.Single(x => x.GetProperty("isCurrent").GetBoolean()).GetProperty("amount").GetDecimal());
    }

    // "It is worth this much" with no date is a statement about now, so it can never be stale - and it
    // leaves the asset with no appraisal date rather than inventing today as one.
    [Fact]
    public async Task An_undated_valuation_replaces_a_stated_one_and_leaves_no_appraisal_date()
    {
        using var factory = new BackendWebApplicationFactory();
        var scenario = await SeedAsync(factory, new DateOnly(2026, 8, 1));
        using var client = factory.CreateClient();

        using var accept = await client.SendAsync(Request(HttpMethod.Post, $"/api/assets/{scenario.Asset}/valuations?fullWorthSpaceId={scenario.Space}", scenario.Owner,
            new { amount = 111_000m, currency = "EUR", method = "manual", isAccepted = true }));
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        using (var json = JsonDocument.Parse(await accept.Content.ReadAsStringAsync()))
            Assert.False(json.RootElement.GetProperty("valuedAtIsStated").GetBoolean());

        await factory.SeedAsync(async db =>
        {
            var asset = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == scenario.Asset);
            Assert.Equal(111_000m, asset.CurrentValue);
            Assert.Null(asset.ValuedAt);
            Assert.InRange(asset.ValueRecordedAt, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        });
    }

    private static Task AssertSeedValueUnchangedAsync(BackendWebApplicationFactory factory, Scenario scenario) =>
        factory.SeedAsync(async db =>
        {
            var asset = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == scenario.Asset);
            Assert.Equal(100_000m, asset.CurrentValue);
            Assert.Equal("EUR", asset.Currency);
            Assert.Equal(new DateOnly(2026, 8, 1), asset.ValuedAt);
        });

    private static async Task<JsonElement[]> HistoryAsync(HttpClient client, Scenario scenario, Guid assetId)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/assets/{assetId}/valuations?fullWorthSpaceId={scenario.Space}", scenario.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Guid userId, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory, DateOnly? valuedAt)
    {
        var scenario = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = scenario.Owner,
                EmailNormalized = $"{scenario.Owner:N}@EXAMPLE.COM",
                DisplayName = "As-of owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = scenario.Space, Name = "As-of space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = scenario.Space,
                UserId = scenario.Owner,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Assets.Add(new Asset
            {
                Id = scenario.Asset,
                FullWorthSpaceId = scenario.Space,
                Name = "As-of house",
                Kind = AssetKinds.RealEstate,
                CurrentValue = 100_000m,
                Currency = "EUR",
                ValuedAt = valuedAt,
                IncludeInNetWorth = true
            });
            await db.SaveChangesAsync();
        });
        return scenario;
    }

    private sealed record Scenario(Guid Owner, Guid Space, Guid Asset);
}
