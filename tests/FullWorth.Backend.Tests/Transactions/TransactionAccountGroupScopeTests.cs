using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Transactions;

/// <summary>
/// AccountGroupId scope + authorization for the transaction search (§3), plus the resolved presentation
/// identity on the list DTO (§4/§7). Runs on the in-memory SQLite model (no Postgres): it exercises the
/// TransactionStore query directly, proving a caller can never read another space's/account's rows via a
/// foreign groupId and that merchant/brand/category identity is server-resolved.
/// </summary>
public sealed class TransactionAccountGroupScopeTests
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task GroupScope_ReturnsOnlyThatGroupsTransactions()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        var result = await store.SearchForUserAsync(s.UserA, s.SpaceA, Query(groupId: s.GroupDaily), default);
        using var json = Parse(result);

        Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
        var item = json.RootElement.GetProperty("items")[0];
        Assert.Equal(s.TxDaily, item.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task ForeignGroupId_ReturnsNothing_NotAnotherSpacesRows()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        // GroupOther belongs to SpaceB / UserB. UserA must not be able to read UserB's transactions by
        // supplying that group id — the group is resolved only against UserA's accessible accounts.
        var result = await store.SearchForUserAsync(s.UserA, s.SpaceA, Query(groupId: s.GroupOther), default);
        using var json = Parse(result);
        Assert.Equal(0, json.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("items").EnumerateArray());

        // Even with no space pin (null fullWorthSpaceId) the foreign group stays invisible.
        var unpinned = await store.SearchForUserAsync(s.UserA, null, Query(groupId: s.GroupOther), default);
        using var unpinnedJson = Parse(unpinned);
        Assert.Equal(0, unpinnedJson.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ListDto_CarriesResolvedMerchantBrandAndCategoryIcon()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        var result = await store.SearchForUserAsync(s.UserA, s.SpaceA, Query(groupId: s.GroupDaily), default);
        using var json = Parse(result);
        var item = json.RootElement.GetProperty("items")[0];

        // REWE alias → merchant → curated brand, resolved server-side.
        Assert.Equal(s.Rewe, item.GetProperty("merchantId").GetGuid());
        Assert.Equal("REWE", item.GetProperty("merchantDisplayName").GetString());
        Assert.Equal("rewe", item.GetProperty("brandKey").GetString());
        Assert.Equal("brands/rewe.svg", item.GetProperty("logoAssetPath").GetString());
        // Category identity for the fallback path.
        Assert.Equal("Groceries", item.GetProperty("categoryName").GetString());
        Assert.Equal("groceries", item.GetProperty("categoryIconKey").GetString());
    }

    [Fact]
    public async Task UnknownMerchant_HasNullBrand_ButKeepsCategoryIcon()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        var result = await store.SearchForUserAsync(s.UserA, s.SpaceA, Query(groupId: s.GroupSavings), default);
        using var json = Parse(result);
        var item = json.RootElement.GetProperty("items")[0];

        Assert.Equal(JsonValueKind.Null, item.GetProperty("brandKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("merchantId").ValueKind);
        Assert.Equal("transport", item.GetProperty("categoryIconKey").GetString());
    }

    private static TransactionQuery Query(Guid? groupId = null, Guid? accountId = null) =>
        new(accountId, groupId, null, null, null, null, null, null, null, null, null, null, null, null);

    private static JsonDocument Parse(object result) => JsonDocument.Parse(JsonSerializer.Serialize(result, Camel));

    private sealed record Seed(
        Guid SpaceA, Guid SpaceB, Guid UserA, Guid UserB, Guid AccountDaily, Guid AccountSavings, Guid AccountOther,
        Guid GroupDaily, Guid GroupSavings, Guid GroupOther, Guid TxDaily, Guid TxSavings, Guid Rewe, Guid Groceries, Guid Transport);

    private static async Task<Seed> SeedAsync(SqliteFullWorthDatabase database)
    {
        var s = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using var db = database.CreateContext();
        foreach (var (id, space) in new[] { (s.UserA, s.SpaceA), (s.UserB, s.SpaceB) })
        {
            db.Users.Add(new FullWorthUser { Id = id, EmailNormalized = $"{id:N}@EX.COM".ToUpperInvariant(), DisplayName = "U", IsActive = true });
        }
        db.FullWorthSpaces.AddRange(
            new FullWorthSpace { Id = s.SpaceA, Name = "A", BaseCurrency = "EUR" },
            new FullWorthSpace { Id = s.SpaceB, Name = "B", BaseCurrency = "EUR" });
        db.FullWorthSpaceMembers.AddRange(
            new FullWorthSpaceMember { FullWorthSpaceId = s.SpaceA, UserId = s.UserA, Role = FullWorthSpaceRoles.Owner },
            new FullWorthSpaceMember { FullWorthSpaceId = s.SpaceB, UserId = s.UserB, Role = FullWorthSpaceRoles.Owner });

        db.AccountGroups.AddRange(
            new AccountGroup { Id = s.GroupDaily, FullWorthSpaceId = s.SpaceA, Name = "Daily" },
            new AccountGroup { Id = s.GroupSavings, FullWorthSpaceId = s.SpaceA, Name = "Savings" },
            new AccountGroup { Id = s.GroupOther, FullWorthSpaceId = s.SpaceB, Name = "Foreign" });

        AddAccount(db, s.AccountDaily, s.SpaceA, s.UserA, s.GroupDaily, "Giro");
        AddAccount(db, s.AccountSavings, s.SpaceA, s.UserA, s.GroupSavings, "Spar");
        AddAccount(db, s.AccountOther, s.SpaceB, s.UserB, s.GroupOther, "Foreign");

        db.Categories.AddRange(
            new FinanceCategory { Id = s.Groceries, FullWorthSpaceId = s.SpaceA, Key = "groceries", Name = "Groceries" },
            new FinanceCategory { Id = s.Transport, FullWorthSpaceId = s.SpaceA, Key = "transport", Name = "Transport" });

        db.Merchants.Add(new Merchant { Id = s.Rewe, FullWorthSpaceId = s.SpaceA, Name = "REWE", NormalizedName = "REWE" });
        db.MerchantAliases.Add(new MerchantAlias { MerchantId = s.Rewe, FullWorthSpaceId = s.SpaceA, NormalizedAlias = "REWE" });

        AddTx(db, s.TxDaily, s.AccountDaily, s.Groceries, "REWE SAGT DANKE 123", -42m, new DateOnly(2026, 8, 5));
        AddTx(db, s.TxSavings, s.AccountSavings, s.Transport, "SOME LOCAL CORNER SHOP", -9m, new DateOnly(2026, 8, 6));
        AddTx(db, Guid.NewGuid(), s.AccountOther, null, "ALDI SUED", -12m, new DateOnly(2026, 8, 7));

        await db.SaveChangesAsync();
        return s;
    }

    private static void AddAccount(FullWorthDbContext db, Guid id, Guid spaceId, Guid ownerId, Guid groupId, string name)
    {
        db.Accounts.Add(new FinanceAccount
        {
            Id = id,
            FullWorthSpaceId = spaceId,
            Provider = "manual",
            IdentificationHash = $"m-{id:N}",
            ProviderAccountId = $"m-{id:N}",
            InstitutionName = "Cash",
            DisplayName = name,
            Currency = "EUR",
            GroupId = groupId
        });
        db.AccountOwners.Add(new AccountOwner { AccountId = id, UserId = ownerId, OwnershipType = AccountOwnershipTypes.Owner });
    }

    private static void AddTx(FullWorthDbContext db, Guid id, Guid accountId, Guid? categoryId, string counterparty, decimal amount, DateOnly date) =>
        db.Transactions.Add(new FinanceTransaction
        {
            Id = id,
            AccountId = accountId,
            CategoryId = categoryId,
            ExternalKey = $"tx-{id:N}",
            Amount = amount,
            Currency = "EUR",
            Counterparty = counterparty,
            NormalizedCounterparty = MerchantNormalization.Normalize(counterparty),
            BookingDate = date,
            RawJson = "{}"
        });
}
