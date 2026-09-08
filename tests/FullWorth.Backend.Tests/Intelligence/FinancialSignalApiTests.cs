using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Signals;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class FinancialSignalApiTests
{
    [Fact]
    public async Task Insights_are_user_scoped_and_dismissal_moves_them_to_hidden()
    {
        using var factory = InsightsOnFactory();
        using var client = factory.CreateClient();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await SeedMemberAsync(factory, userA);
        await SeedMemberAsync(factory, userB);

        Guid signalId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<FinancialSignalStore>();
            var detected = new DetectedFinancialSignal(
                FullWorthSpaceDefaults.LegacyId,
                userA,
                "spending-shift",
                "merchant",
                "REWE",
                "merchant-spike:REWE:2026-09",
                "deterministic",
                FinancialSignalSeverities.Attention,
                .9m,
                35m,
                "EUR",
                "insights.spendingShift",
                "{\"delta\":35}",
                "{\"baseline\":100,\"current\":135}",
                50m,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(7));
            signalId = (await store.UpsertAsync(detected, CancellationToken.None)).Id;
        }

        using (var request = UserRequest(
                   HttpMethod.Get,
                   $"/api/insights?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
                   userA))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal(signalId, json.RootElement[0].GetProperty("id").GetGuid());
        }

        using (var request = UserRequest(
                   HttpMethod.Get,
                   $"/api/insights/{signalId}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
                   userB))
        using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using (var request = UserRequest(
                   HttpMethod.Post,
                   $"/api/insights/{signalId}/dismiss?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
                   userA))
        using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var request = UserRequest(
                   HttpMethod.Get,
                   $"/api/insights?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&view=active",
                   userA))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Empty(json.RootElement.EnumerateArray());
        }

        using (var request = UserRequest(
                   HttpMethod.Get,
                   $"/api/insights?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}&view=hidden",
                   userA))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal("dismissed", json.RootElement[0].GetProperty("state").GetString());
        }
    }

    [Fact]
    public async Task Non_member_cannot_query_a_space()
    {
        using var factory = InsightsOnFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        await factory.SeedFullWorthUserAsync(userId);

        using var request = UserRequest(
            HttpMethod.Get,
            $"/api/insights?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Insights_api_is_hidden_while_signals_run_in_shadow()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        await SeedMemberAsync(factory, userId);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<FinancialSignalStore>();
            await store.UpsertAsync(new DetectedFinancialSignal(
                FullWorthSpaceDefaults.LegacyId,
                userId,
                "data-quality",
                "financial-context",
                FullWorthSpaceDefaults.LegacyId.ToString("N"),
                "data-quality:incomplete",
                "detector:data-quality",
                FinancialSignalSeverities.Info,
                1m,
                null,
                null,
                "insights.data.incomplete",
                "{}",
                "{}",
                30m,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(7)), CancellationToken.None);
        }

        using var request = UserRequest(
            HttpMethod.Get,
            $"/api/insights?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",
            userId);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static BackendWebApplicationFactory InsightsOnFactory() =>
        new(new Dictionary<string, string?>
        {
            [$"{AutopilotRolloutSettings.SectionName}:{AutopilotFeatures.Insights}"] = "on"
        });

    private static async Task SeedMemberAsync(BackendWebApplicationFactory factory, Guid userId)
    {
        await factory.SeedFullWorthUserAsync(userId);
        await factory.SeedAsync(async db =>
        {
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Member
            });
            await db.SaveChangesAsync();
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }
}
