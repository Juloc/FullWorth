using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

/// <summary>
/// The trend converts every historical day at its own date. A day whose foreign account has no rate used
/// to be summed WITHOUT that account: the point kept a number, so the curve dipped or read flat and the
/// drawn value was simply too low. A sum that is missing a whole account is not a smaller net worth — it is
/// an unknown one, and the chart already drops null points and draws a gap.
/// </summary>
public sealed class WealthHistoryCurrencyGapTests
{
    private static readonly DateOnly Day = new(2026, 7, 1);

    [Fact]
    public async Task A_day_with_a_missing_rate_reports_an_unknown_net_worth_not_a_partial_one()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedAsync(factory, withIdrRate: false);
        using var client = factory.CreateClient();

        var point = await HistoryPointAsync(client, owner, space);

        Assert.Equal(JsonValueKind.Null, point.GetProperty("netWorth").ValueKind);
        Assert.Equal(JsonValueKind.Null, point.GetProperty("accounts").ValueKind);
        Assert.False(point.GetProperty("isComplete").GetBoolean());
        Assert.Contains(
            "IDR",
            point.GetProperty("missingCurrencies").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task With_the_rate_present_the_foreign_account_is_part_of_the_curve()
    {
        using var factory = new BackendWebApplicationFactory();
        var (owner, space) = await SeedAsync(factory, withIdrRate: true);
        using var client = factory.CreateClient();

        var point = await HistoryPointAsync(client, owner, space);

        // 100 EUR + 5,000,000 IDR at 20,000 IDR per EUR = 350 EUR.
        Assert.Equal(350m, point.GetProperty("accounts").GetDecimal());
        Assert.Equal(350m, point.GetProperty("netWorth").GetDecimal());
        Assert.True(point.GetProperty("isComplete").GetBoolean());
    }

    private static async Task<(Guid Owner, Guid Space)> SeedAsync(
        BackendWebApplicationFactory factory, bool withIdrRate)
    {
        var owner = Guid.NewGuid();
        var space = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = owner,
                EmailNormalized = $"{owner:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Trend owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = space, Name = "Trend", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Role = FullWorthSpaceRoles.Owner
            });

            // One snapshot row per currency for the same day, exactly as the snapshot service writes them.
            db.NetWorthSnapshots.Add(new NetWorthSnapshot
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Date = Day,
                Currency = "EUR",
                Accounts = 100m,
                Assets = 0m,
                Liabilities = 0m,
                NetWorth = 100m
            });
            db.NetWorthSnapshots.Add(new NetWorthSnapshot
            {
                FullWorthSpaceId = space,
                UserId = owner,
                Date = Day,
                Currency = "IDR",
                Accounts = 5_000_000m,
                Assets = 0m,
                Liabilities = 0m,
                NetWorth = 5_000_000m
            });

            if (withIdrRate)
                db.FxRates.Add(new FxRate { Date = Day, Currency = "IDR", Rate = 20_000m });
            await db.SaveChangesAsync();

            // Promote the EUR row to a V2 carrier so the explicit history path runs - that is the path every
            // snapshot written since the V2 upgrade takes.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "NetWorthSnapshots"
                SET "ManualAssets" = 0, "Investments" = 0, "Loans" = 0, "OtherLiabilities" = 0,
                    "ComponentCurrency" = 'EUR', "IsComplete" = true,
                    "MissingCurrenciesJson" = CAST('[]' AS jsonb)
                WHERE "FullWorthSpaceId" = {space} AND "Date" = {Day} AND "Currency" = 'EUR';
                """);
        });

        return (owner, space);
    }

    private static async Task<JsonElement> HistoryPointAsync(HttpClient client, Guid owner, Guid space)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/wealth/history?fullWorthSpaceId={space}&from={Day:yyyy-MM-dd}&to={Day:yyyy-MM-dd}&currency=EUR");
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", owner.ToString("D"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Assert.Single(document.RootElement.EnumerateArray().ToArray()).Clone();
    }
}
