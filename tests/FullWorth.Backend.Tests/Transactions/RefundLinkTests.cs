using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using Microsoft.EntityFrameworkCore;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Transactions;

// Refund linking (UI_UX_SPEC §9.6): a positive transaction may link to an original expense; the link
// is validated (refund must be an inflow, original must be an expense, not itself) and can be cleared.
public sealed class RefundLinkTests
{
    [Fact]
    public async Task LinksRefundToExpenseAndClears()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        Assert.Equal(RefundLinkResult.Updated, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Refund, s.Expense, null, CancellationToken.None));
        Assert.Equal(s.Expense, await db.Transactions.Where(x => x.Id == s.Refund).Select(x => x.RefundOfTransactionId).SingleAsync());

        Assert.Equal(RefundLinkResult.Updated, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Refund, null, null, CancellationToken.None));
        Assert.Null(await db.Transactions.Where(x => x.Id == s.Refund).Select(x => x.RefundOfTransactionId).SingleAsync());
    }

    [Fact]
    public async Task RejectsInvalidLinks()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        // Original is not an expense (positive) -> invalid.
        Assert.Equal(RefundLinkResult.Invalid, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Refund, s.Refund2, null, CancellationToken.None));
        // Linking a refund to itself -> invalid.
        Assert.Equal(RefundLinkResult.Invalid, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Refund, s.Refund, null, CancellationToken.None));
        // The "refund" is actually an expense (negative) -> invalid.
        Assert.Equal(RefundLinkResult.Invalid, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Expense, s.Expense2, null, CancellationToken.None));
        // Unknown original -> not found.
        Assert.Equal(RefundLinkResult.NotFound, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Refund, Guid.NewGuid(), null, CancellationToken.None));
        // Cross-currency original (EUR refund -> USD expense) -> invalid.
        Assert.Equal(RefundLinkResult.Invalid, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Refund, s.UsdExpense, null, CancellationToken.None));
        // Transfer original -> invalid (a transfer leg is not an expense).
        Assert.Equal(RefundLinkResult.Invalid, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Refund, s.TransferExpense, null, CancellationToken.None));
    }

    // §9.6 targeting: a refund may name a SPECIFIC category of the original — its own category or a split
    // line. A valid target is stored; a category not on the original is rejected; clearing wipes both.
    [Fact]
    public async Task TargetedRefundLinkStoresAndValidatesCategory()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        // s.Expense is split into CatA (-30) / CatB (-20); linking to CatB is valid and persists.
        Assert.Equal(RefundLinkResult.Updated, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Refund, s.Expense, s.CatB, CancellationToken.None));
        Assert.Equal(s.CatB, await db.Transactions.Where(x => x.Id == s.Refund).Select(x => x.RefundCategoryId).SingleAsync());

        // A category that is not on the original (neither its own nor a split line) -> invalid.
        Assert.Equal(RefundLinkResult.Invalid, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Refund, s.Expense, Guid.NewGuid(), CancellationToken.None));

        // Clearing the link nulls BOTH the original link and the targeted category.
        Assert.Equal(RefundLinkResult.Updated, await store.LinkRefundForOwnerAsync(s.User, FullWorthSpaceDefaults.LegacyId, s.Refund, null, null, CancellationToken.None));
        var cleared = await db.Transactions.Where(x => x.Id == s.Refund).Select(x => new { x.RefundOfTransactionId, x.RefundCategoryId }).SingleAsync();
        Assert.Null(cleared.RefundOfTransactionId);
        Assert.Null(cleared.RefundCategoryId);
    }

    private sealed record Seed(Guid User, Guid Expense, Guid Expense2, Guid Refund, Guid Refund2, Guid UsdExpense, Guid TransferExpense, Guid CatA, Guid CatB);

    private static async Task<Seed> SeedAsync(SqliteFullWorthDatabase database)
    {
        var s = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = database.CreateContext();
        var connection = new BankConnection { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = $"refund-{s.User:N}" };
        var account = new FinanceAccount { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, BankConnectionId = connection.Id, Provider = "test", IdentificationHash = $"refund-{s.User:N}", ProviderAccountId = $"refund-{s.User:N}", InstitutionName = "Bank", DisplayName = "Acc", Currency = "EUR" };
        db.Users.Add(new FullWorthUser { Id = s.User, EmailNormalized = $"{s.User:N}@EX.COM".ToUpperInvariant(), DisplayName = "Owner", IsActive = true });
        db.BankConnections.Add(connection);
        db.Accounts.Add(account);
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, UserId = s.User, Role = FullWorthSpaceRoles.Member });
        db.AccountOwners.Add(new AccountOwner { AccountId = account.Id, UserId = s.User, OwnershipType = AccountOwnershipTypes.Owner });
        void Tx(Guid id, decimal amount, string currency = "EUR", bool transfer = false) => db.Transactions.Add(new FinanceTransaction { Id = id, AccountId = account.Id, ExternalKey = $"tx-{id:N}", Amount = amount, Currency = currency, BookingDate = new DateOnly(2026, 6, 1), Status = "BOOK", IsTransfer = transfer });
        Tx(s.Expense, -50m);
        Tx(s.Expense2, -20m);
        Tx(s.Refund, 20m);
        Tx(s.Refund2, 15m);
        Tx(s.UsdExpense, -30m, currency: "USD");
        Tx(s.TransferExpense, -40m, transfer: true);
        // Split the original expense into two categories so a refund can target one of them (§9.6).
        db.Categories.Add(new FinanceCategory { Id = s.CatA, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = $"a-{s.CatA:N}", Name = "A" });
        db.Categories.Add(new FinanceCategory { Id = s.CatB, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = $"b-{s.CatB:N}", Name = "B" });
        db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = s.Expense, CategoryId = s.CatA, Amount = -30m });
        db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = s.Expense, CategoryId = s.CatB, Amount = -20m });
        await db.SaveChangesAsync();
        return s;
    }
}
