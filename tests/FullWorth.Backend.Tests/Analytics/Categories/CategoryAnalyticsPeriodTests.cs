using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Analytics.Categories;

/// <summary>
/// §6 period-respecting category analytics (Postgres integration — the shared ExpenseAllocationBuilder
/// uses SQL APPLY, unsupported on SQLite). Asking for a PAST quarter must return that quarter's spend with
/// the immediately preceding quarter as the comparison, never the current calendar month; and the
/// account-group scope must limit the report to that group's accessible accounts.
/// </summary>
public sealed class CategoryAnalyticsPeriodTests
{
    [Fact]
    public async Task PastQuarter_ReturnsThatQuarterNotCurrentMonth()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            $"/api/analytics/categories?fullWorthSpaceId={s.Space}&from=2026-01-01&to=2026-03-31&granularity=quarter", s.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2026-01-01", json.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-03-31", json.RootElement.GetProperty("to").GetString());
        Assert.Equal("quarter", json.RootElement.GetProperty("granularity").GetString());

        var food = json.RootElement.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Food");
        Assert.Equal(300m, food.GetProperty("current").GetDecimal());  // Q1 2026 only (Aug -999 is outside)
        Assert.Equal(100m, food.GetProperty("previous").GetDecimal()); // previous quarter = Q4 2025
        Assert.Equal(200m, food.GetProperty("trendAbsolute").GetDecimal());
    }

    [Fact]
    public async Task AccountGroupScope_LimitsSpendToThatGroup()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            $"/api/analytics/categories?fullWorthSpaceId={s.Space}&from=2026-01-01&to=2026-03-31&granularity=quarter&accountGroupId={s.Group}", s.Owner));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var food = json.RootElement.GetProperty("categories").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Food");
        Assert.Equal(200m, food.GetProperty("current").GetDecimal()); // only the grouped account's Q1 spend
    }

    private static HttpRequestMessage Request(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private sealed record Scenario(Guid Space, Guid Owner, Guid GroupedAccount, Guid UngroupedAccount, Guid Group, Guid Food);

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser { Id = s.Owner, EmailNormalized = $"{s.Owner:N}@EX.COM".ToUpperInvariant(), DisplayName = "U", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = s.Space, Name = "S", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = s.Space, UserId = s.Owner, Role = FullWorthSpaceRoles.Owner });
            db.AccountGroups.Add(new AccountGroup { Id = s.Group, FullWorthSpaceId = s.Space, Name = "Daily" });

            AddAccount(db, s.GroupedAccount, s.Space, s.Owner, s.Group);
            AddAccount(db, s.UngroupedAccount, s.Space, s.Owner, null);
            db.Categories.Add(new FinanceCategory { Id = s.Food, FullWorthSpaceId = s.Space, Key = "food", Name = "Food" });

            AddTx(db, s.GroupedAccount, s.Food, -200m, new DateOnly(2026, 2, 15));
            AddTx(db, s.UngroupedAccount, s.Food, -100m, new DateOnly(2026, 2, 20));
            AddTx(db, s.GroupedAccount, s.Food, -100m, new DateOnly(2025, 11, 10)); // Q4 2025 (comparison)
            AddTx(db, s.GroupedAccount, s.Food, -999m, new DateOnly(2026, 8, 20));  // decoy outside window

            await db.SaveChangesAsync();
        });
        return s;
    }

    private static void AddAccount(FullWorth.Backend.Data.FullWorthDbContext db, Guid id, Guid spaceId, Guid ownerId, Guid? groupId)
    {
        db.Accounts.Add(new FinanceAccount
        {
            Id = id,
            FullWorthSpaceId = spaceId,
            Provider = "manual",
            IdentificationHash = $"m-{id:N}",
            ProviderAccountId = $"m-{id:N}",
            InstitutionName = "Cash",
            DisplayName = "Acc",
            Currency = "EUR",
            GroupId = groupId
        });
        db.AccountOwners.Add(new AccountOwner { AccountId = id, UserId = ownerId, OwnershipType = AccountOwnershipTypes.Owner });
    }

    private static void AddTx(FullWorth.Backend.Data.FullWorthDbContext db, Guid accountId, Guid categoryId, decimal amount, DateOnly date) =>
        db.Transactions.Add(new FinanceTransaction
        {
            AccountId = accountId,
            CategoryId = categoryId,
            ExternalKey = $"tx-{Guid.NewGuid():N}",
            Amount = amount,
            Currency = "EUR",
            BookingDate = date,
            RawJson = "{}"
        });
}
