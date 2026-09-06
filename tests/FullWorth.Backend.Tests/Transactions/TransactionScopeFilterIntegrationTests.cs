using System.Net;
using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Transactions;

public sealed class TransactionScopeFilterIntegrationTests
{
    [Fact]
    public async Task AccountGroupScopeIsServerSideAndAuthorizationSafe()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={s.Space}&accountGroupId={s.Group}&status=booked&direction=expense",
            s.User));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ids = json.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(s.GroupChild, ids);
        Assert.Contains(s.OriginalExpense, ids);
        Assert.DoesNotContain(s.OtherParent, ids);
        Assert.DoesNotContain(s.Ignored, ids);

        using var inaccessible = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={s.Space}&accountGroupId={Guid.NewGuid():D}",
            s.User));
        using var inaccessibleJson = JsonDocument.Parse(await inaccessible.Content.ReadAsStringAsync());
        Assert.Equal(0, inaccessibleJson.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ParentCategoryCanIncludeDescendantsWithoutDuplicates()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var direct = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={s.Space}&categoryId={s.Parent}&status=booked&direction=expense",
            s.User));
        using var directJson = JsonDocument.Parse(await direct.Content.ReadAsStringAsync());
        Assert.Equal(1, directJson.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(s.OtherParent, directJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());

        using var subtree = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={s.Space}&categoryId={s.Parent}&includeDescendants=true&status=booked&direction=expense",
            s.User));
        using var subtreeJson = JsonDocument.Parse(await subtree.Content.ReadAsStringAsync());
        var ids = subtreeJson.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Contains(s.OtherParent, ids);
        Assert.Contains(s.GroupChild, ids);
        Assert.Contains(s.OriginalExpense, ids);
    }

    [Fact]
    public async Task AdvancedFiltersAreAppliedBeforePaging()
    {
        using var factory = new BackendWebApplicationFactory();
        var s = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={s.Space}&accountGroupId={s.Group}&merchant=REWE&minAmount=40&maxAmount=55&status=booked&direction=expense&limit=1",
            s.User));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(s.GroupChild, json.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());

        using var receipt = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={s.Space}&hasReceipt=true",
            s.User));
        using var receiptJson = JsonDocument.Parse(await receipt.Content.ReadAsStringAsync());
        Assert.Equal(1, receiptJson.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(s.GroupChild, receiptJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());

        using var refund = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={s.Space}&refundOnly=true",
            s.User));
        using var refundJson = JsonDocument.Parse(await refund.Content.ReadAsStringAsync());
        Assert.Equal(1, refundJson.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(s.Refund, refundJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());

        using var ignored = await client.SendAsync(Request(
            $"/api/transactions?fullWorthSpaceId={s.Space}&ignoredOnly=true&includeIgnored=true",
            s.User));
        using var ignoredJson = JsonDocument.Parse(await ignored.Content.ReadAsStringAsync());
        Assert.Equal(1, ignoredJson.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(s.Ignored, ignoredJson.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    private static HttpRequestMessage Request(string path, Guid userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-FullWorth-Internal-Key", BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id", userId.ToString("D"));
        return request;
    }

    private static async Task<Scenario> SeedAsync(BackendWebApplicationFactory factory)
    {
        var s = new Scenario(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = s.User,
                EmailNormalized = $"{s.User:N}@EXAMPLE.COM".ToUpperInvariant(),
                DisplayName = "Scope filter user",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = s.Space, Name = "Scope filters", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = s.Space,
                UserId = s.User,
                Role = FullWorthSpaceRoles.Owner
            });
            db.BankConnections.Add(new BankConnection
            {
                Id = s.Connection,
                FullWorthSpaceId = s.Space,
                Provider = "test",
                InstitutionName = "Scope Bank",
                Country = "DE",
                ProviderSessionId = $"scope-{s.Connection:N}",
                Status = "AUTHORIZED"
            });
            db.AccountGroups.Add(new AccountGroup
            {
                Id = s.Group,
                FullWorthSpaceId = s.Space,
                Name = "Daily accounts",
                SortOrder = 0
            });
            db.Accounts.AddRange(
                Account(s.GroupAccount, s.Space, s.Connection, s.Group, "Grouped"),
                Account(s.OtherAccount, s.Space, s.Connection, null, "Other"));
            db.AccountOwners.AddRange(
                new AccountOwner { AccountId = s.GroupAccount, UserId = s.User, OwnershipType = AccountOwnershipTypes.Owner },
                new AccountOwner { AccountId = s.OtherAccount, UserId = s.User, OwnershipType = AccountOwnershipTypes.Owner });

            db.Categories.AddRange(
                new FinanceCategory { Id = s.Parent, FullWorthSpaceId = s.Space, Key = "parent", Name = "Food" },
                new FinanceCategory { Id = s.Child, FullWorthSpaceId = s.Space, ParentId = s.Parent, Key = "child", Name = "Groceries" });

            db.Transactions.AddRange(
                Tx(s.GroupChild, s.GroupAccount, s.Child, -50m, "REWE", "BOOK"),
                Tx(s.Pending, s.GroupAccount, s.Child, -45m, "REWE", "PDNG"),
                Tx(s.OtherParent, s.OtherAccount, s.Parent, -20m, "OTHER", "BOOK"),
                Tx(s.Ignored, s.GroupAccount, s.Child, -70m, "REWE", "BOOK", ignored: true),
                Tx(s.OriginalExpense, s.GroupAccount, s.Child, -30m, "SHOP", "BOOK"),
                Tx(s.Refund, s.GroupAccount, s.Child, 10m, "SHOP", "BOOK", refundOf: s.OriginalExpense));

            db.Purchases.Add(new Purchase
            {
                Id = Guid.NewGuid(),
                FullWorthSpaceId = s.Space,
                TransactionId = s.GroupChild,
                Source = "receipt",
                Merchant = "REWE",
                PurchaseDate = new DateOnly(2026, 9, 1),
                TotalAmount = 50m,
                Currency = "EUR",
                Status = "confirmed",
                ReviewState = "confirmed",
                CreatedByUserId = s.User,
                Visibility = "space"
            });

            await db.SaveChangesAsync();
        });
        return s;
    }

    private static FinanceAccount Account(Guid id, Guid space, Guid connection, Guid? group, string name) => new()
    {
        Id = id,
        FullWorthSpaceId = space,
        BankConnectionId = connection,
        GroupId = group,
        Provider = "test",
        IdentificationHash = $"scope-{id:N}",
        ProviderAccountId = $"scope-{id:N}",
        InstitutionName = "Scope Bank",
        DisplayName = name,
        Currency = "EUR"
    };

    private static FinanceTransaction Tx(
        Guid id,
        Guid accountId,
        Guid? categoryId,
        decimal amount,
        string merchant,
        string status,
        bool ignored = false,
        Guid? refundOf = null) => new()
    {
        Id = id,
        AccountId = accountId,
        CategoryId = categoryId,
        ExternalKey = $"scope-{id:N}",
        Amount = amount,
        Currency = "EUR",
        BookingDate = new DateOnly(2026, 9, 1),
        Counterparty = merchant,
        NormalizedCounterparty = merchant,
        Status = status,
        IsIgnored = ignored,
        RefundOfTransactionId = refundOf,
        RawJson = "{}"
    };

    private sealed record Scenario(
        Guid User,
        Guid Space,
        Guid Connection,
        Guid Group,
        Guid GroupAccount,
        Guid OtherAccount,
        Guid Parent,
        Guid Child,
        Guid GroupChild,
        Guid Pending,
        Guid OtherParent,
        Guid Ignored,
        Guid OriginalExpense,
        Guid Refund);
}
