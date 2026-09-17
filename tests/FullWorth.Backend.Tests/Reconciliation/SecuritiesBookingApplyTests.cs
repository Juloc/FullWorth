using System.Net;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FullWorth.Backend.Tests.Reconciliation;

/// <summary>
/// Der Buchungs-Abgleich Ende-zu-Ende: Buchungen des Verrechnungskontos werden gegen ein Wertpapier
/// abgeglichen und als "buy"-Handel uebernommen, ohne den von FinTS gemeldeten Bestand doppelt zu
/// zaehlen (SnapshotRestRule).
/// </summary>
public sealed class SecuritiesBookingApplyTests
{
    [Fact]
    public async Task Applying_matched_bookings_that_exactly_explain_the_snapshot_deletes_it_and_avoids_double_counting()
    {
        using var scenario = await SeedAsync();
        var booking1 = await AddBookingAsync(scenario, -400m, new DateOnly(2026, 8, 1), "Kauf Wertpapier WKN 716460 Boersenauftrag");
        var booking2 = await AddBookingAsync(scenario, -600m, new DateOnly(2026, 8, 2), "Kauf Wertpapier WKN 716460 Boersenauftrag");

        var apply = await ApplyAsync(scenario, [booking1, booking2]);
        Assert.Equal(2, apply.GetProperty("applied").GetArrayLength());
        Assert.Equal(0, apply.GetProperty("alreadyApplied").GetArrayLength());
        Assert.Equal(0, apply.GetProperty("rejected").GetArrayLength());

        var trades = await LoadTradesAsync(scenario);
        Assert.DoesNotContain(trades, trade => trade.Source == "fints_snapshot");
        Assert.Equal(2, trades.Count(trade => trade is { TradeType: "buy", Source: "bank_booking" }));
        Assert.Equal(10m, trades.Sum(trade => trade.Quantity ?? 0m));
    }

    [Fact]
    public async Task Applying_the_same_booking_twice_is_idempotent()
    {
        using var scenario = await SeedAsync();
        var booking = await AddBookingAsync(scenario, -400m, new DateOnly(2026, 8, 1), "Kauf Wertpapier WKN 716460");

        var first = await ApplyAsync(scenario, [booking]);
        Assert.Equal(1, first.GetProperty("applied").GetArrayLength());
        Assert.Equal(0, first.GetProperty("alreadyApplied").GetArrayLength());

        var second = await ApplyAsync(scenario, [booking]);
        Assert.Equal(0, second.GetProperty("applied").GetArrayLength());
        Assert.Equal(1, second.GetProperty("alreadyApplied").GetArrayLength());

        var trades = await LoadTradesAsync(scenario);
        Assert.Single(trades, trade => trade.ExternalKey == $"booking:{booking:N}");
    }

    [Fact]
    public async Task Suggestions_surface_the_matchers_confidence_flag_unchanged()
    {
        using var scenario = await SeedAsync();
        await AddBookingAsync(scenario, -400m, new DateOnly(2026, 8, 1), "Kauf Wertpapier WKN 716460");
        await AddBookingAsync(scenario, -600m, new DateOnly(2026, 8, 2), "Kauf Wertpapier WKN 716460");

        var suggestions = await GetSuggestionsAsync(scenario);
        var matches = suggestions.GetProperty("matches").EnumerateArray().ToList();
        Assert.Equal(2, matches.Count);
        Assert.All(matches, match => Assert.True(match.GetProperty("confident").GetBoolean()));

        var summary = Assert.Single(suggestions.GetProperty("summaries").EnumerateArray());
        Assert.True(summary.GetProperty("confident").GetBoolean());
        Assert.Equal(10m, summary.GetProperty("q").GetDecimal());
    }

    [Fact]
    public async Task Dismissing_a_booking_removes_it_from_the_next_suggestions_call()
    {
        using var scenario = await SeedAsync(withSnapshot: false);
        var booking = await AddBookingAsync(scenario, -250m, new DateOnly(2026, 8, 5), "Kauf Wertpapier WKN 716460");

        var before = await GetSuggestionsAsync(scenario);
        Assert.Single(before.GetProperty("matches").EnumerateArray());

        using var client = scenario.Factory.CreateClient();
        var body = JsonSerializer.Serialize(new { portfolioId = scenario.PortfolioId, transactionIds = new[] { booking } });
        using var request = UserRequest(HttpMethod.Post,
            $"/api/reconciliation/securities-bookings/dismiss?fullWorthSpaceId={scenario.SpaceId:D}", scenario.UserId);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await GetSuggestionsAsync(scenario);
        Assert.Empty(after.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task A_booking_with_no_estimable_quantity_is_rejected_not_silently_dropped()
    {
        using var scenario = await SeedAsync(withSnapshot: false, withPrice: false);
        var booking = await AddBookingAsync(scenario, -250m, new DateOnly(2026, 8, 5), "Kauf Wertpapier WKN 716460");

        var apply = await ApplyAsync(scenario, [booking]);
        Assert.Empty(apply.GetProperty("applied").EnumerateArray());
        Assert.Empty(apply.GetProperty("alreadyApplied").EnumerateArray());
        var rejected = apply.GetProperty("rejected").EnumerateArray().Select(entry => entry.GetGuid()).ToList();
        Assert.Equal([booking], rejected);

        Assert.Empty(await LoadTradesAsync(scenario));
    }

    private sealed record Scenario(
        BackendWebApplicationFactory Factory, Guid UserId, Guid SpaceId, Guid PortfolioId, Guid SecurityId,
        Guid AccountId) : IDisposable
    {
        public void Dispose() => Factory.Dispose();
    }

    private static async Task<Scenario> SeedAsync(bool withSnapshot = true, bool withPrice = true)
    {
        var factory = new BackendWebApplicationFactory();
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var portfolioId = Guid.NewGuid();
        var securityId = Guid.NewGuid();

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@EX.COM".ToUpperInvariant(),
                DisplayName = "Booking owner",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Booking Space", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = spaceId,
                Provider = "manual",
                IdentificationHash = $"booking-{accountId:N}",
                ProviderAccountId = $"booking-{accountId:N}",
                InstitutionName = "Test Bank",
                DisplayName = "Girokonto",
                Currency = "EUR",
                IsActive = true,
                IncludeInNetWorth = true
            });
            await db.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES ({portfolioId},{spaceId},{"Depot"},{"EUR"},{accountId},{true},{true},{false},{now},{now})
""");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "Securities" ("Id","FullWorthSpaceId","Name","Wkn","AssetType","Currency","IsActive","CreatedAt","UpdatedAt")
VALUES ({securityId},{spaceId},{"Testwert AG"},{"716460"},{"stock"},{"EUR"},{true},{now},{now})
""");

            if (withPrice)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt")
VALUES ({securityId},{new DateOnly(2026, 7, 30)},{100m},{"EUR"},{"manual"},{now})
""");

            if (withSnapshot)
                await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","Quantity","Price","GrossAmount","Amount",
 "Currency","Fees","Taxes","WithholdingTax","Source","ExternalKey","CostPrice","CreatedAt","UpdatedAt")
VALUES ({Guid.NewGuid()},{spaceId},{portfolioId},{securityId},{"security_transfer_in"},{new DateOnly(2026, 8, 1)},
        {10m},{100m},{1000m},{0m},{"EUR"},{0m},{0m},{0m},{"fints_snapshot"},{"fints-position:test"},{100m},{now},{now})
""");
        });

        return new Scenario(factory, userId, spaceId, portfolioId, securityId, accountId);
    }

    private static async Task<Guid> AddBookingAsync(Scenario scenario, decimal amount, DateOnly date, string description)
    {
        var id = Guid.NewGuid();
        await scenario.Factory.SeedAsync(async db =>
        {
            db.Transactions.Add(new FinanceTransaction
            {
                Id = id,
                AccountId = scenario.AccountId,
                ExternalKey = id.ToString("N"),
                Amount = amount,
                Currency = "EUR",
                BookingDate = date,
                ValueDate = date,
                Description = description,
                IsIgnored = false,
                IsTransfer = false
            });
            await db.SaveChangesAsync();
        });
        return id;
    }

    private static async Task<JsonElement> GetSuggestionsAsync(Scenario scenario)
    {
        using var client = scenario.Factory.CreateClient();
        using var response = await client.SendAsync(UserRequest(HttpMethod.Get,
            $"/api/reconciliation/securities-bookings?portfolioId={scenario.PortfolioId:D}&fullWorthSpaceId={scenario.SpaceId:D}",
            scenario.UserId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ApplyAsync(Scenario scenario, IReadOnlyList<Guid> transactionIds)
    {
        using var client = scenario.Factory.CreateClient();
        var body = JsonSerializer.Serialize(new { portfolioId = scenario.PortfolioId, transactionIds });
        using var request = UserRequest(HttpMethod.Post,
            $"/api/reconciliation/securities-bookings/apply?fullWorthSpaceId={scenario.SpaceId:D}", scenario.UserId);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static HttpRequestMessage UserRequest(HttpMethod method, string path, Guid userId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record TradeRow(string TradeType, string Source, decimal? Quantity, string? ExternalKey);

    private static async Task<List<TradeRow>> LoadTradesAsync(Scenario scenario)
    {
        await using var connection = new NpgsqlConnection(scenario.Factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT "TradeType","Source","Quantity","ExternalKey" FROM "InvestmentTrades"
WHERE "PortfolioId"=@portfolio AND "SecurityId"=@security
""";
        command.Parameters.AddWithValue("portfolio", scenario.PortfolioId);
        command.Parameters.AddWithValue("security", scenario.SecurityId);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<TradeRow>();
        while (await reader.ReadAsync())
            rows.Add(new TradeRow(
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }
}
