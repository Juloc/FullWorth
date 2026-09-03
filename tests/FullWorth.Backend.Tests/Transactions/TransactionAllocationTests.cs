using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Transactions;

// Transaction split allocations: lines must NET to the transaction total, reject cross-space
// categories, and may include signed adjustments such as coupons that reduce gross article spend.
public sealed class TransactionAllocationTests
{
    [Fact]
    public async Task BalancedSplitRoundTrips()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = Seed.New();

        await using (var db = database.CreateContext())
        {
            await Seed.PopulateAsync(db, s, txAmount: -50m);
            var store = new TransactionStore(db);
            var result = await store.ReplaceAllocationsForOwnerAsync(s.UserId, FullWorthSpaceDefaults.LegacyId, s.TxId,
                new[] { new AllocationLine(s.CatB, -30m, "groceries"), new AllocationLine(s.CatA, -20m, null) }, CancellationToken.None);
            Assert.Equal(AllocationResult.Updated, result);
        }

        await using (var db = database.CreateContext())
        {
            var store = new TransactionStore(db);
            var view = await store.GetAllocationsForUserAsync(s.UserId, FullWorthSpaceDefaults.LegacyId, s.TxId, CancellationToken.None);
            var json = JsonSerializer.SerializeToElement(view!);
            Assert.Equal(2, json.GetProperty("lines").GetArrayLength());
            Assert.Equal(0m, json.GetProperty("remaining").GetDecimal());
        }
    }

    [Fact]
    public async Task UnbalancedSplitIsRejected()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = Seed.New();
        await using var db = database.CreateContext();
        await Seed.PopulateAsync(db, s, txAmount: -50m);

        var store = new TransactionStore(db);
        var result = await store.ReplaceAllocationsForOwnerAsync(s.UserId, FullWorthSpaceDefaults.LegacyId, s.TxId,
            new[] { new AllocationLine(s.CatB, -30m, null), new AllocationLine(s.CatA, -10m, null) }, CancellationToken.None);
        Assert.Equal(AllocationResult.Unbalanced, result);
        Assert.Equal(0, await db.TransactionAllocations.CountAsync(x => x.TransactionId == s.TxId));
    }

    [Fact]
    public async Task MixedSignSplitCanRepresentCouponWhenNetMatchesTransaction()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = Seed.New();
        await using var db = database.CreateContext();
        await Seed.PopulateAsync(db, s, txAmount: -50m);
        var store = new TransactionStore(db);

        // Gross category B spend is 60, a +10 coupon in A reduces the real bank charge to 50.
        var result = await store.ReplaceAllocationsForOwnerAsync(s.UserId, FullWorthSpaceDefaults.LegacyId, s.TxId,
            new[] { new AllocationLine(s.CatB, -60m, "gross"), new AllocationLine(s.CatA, 10m, "coupon") }, CancellationToken.None);

        Assert.Equal(AllocationResult.Updated, result);
        Assert.Equal(-50m, await db.TransactionAllocations.Where(x => x.TransactionId == s.TxId).SumAsync(x => x.Amount));
    }

    [Fact]
    public async Task CrossSpaceCategoryIsRejected()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = Seed.New();
        await using var db = database.CreateContext();
        await Seed.PopulateAsync(db, s, txAmount: -50m);
        var otherSpace = new FullWorthSpace { Name = "Other", BaseCurrency = "EUR" };
        var foreignCategory = new FinanceCategory { FullWorthSpaceId = otherSpace.Id, Key = "foreign", Name = "Foreign" };
        db.AddRange(otherSpace, foreignCategory);
        await db.SaveChangesAsync();

        var store = new TransactionStore(db);
        var result = await store.ReplaceAllocationsForOwnerAsync(s.UserId, FullWorthSpaceDefaults.LegacyId, s.TxId,
            new[] { new AllocationLine(foreignCategory.Id, -50m, null) }, CancellationToken.None);
        Assert.Equal(AllocationResult.InvalidCategory, result);
    }

    [Fact]
    public async Task EmptyListClearsSplit()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = Seed.New();
        await using var db = database.CreateContext();
        await Seed.PopulateAsync(db, s, txAmount: -50m);
        var store = new TransactionStore(db);

        await store.ReplaceAllocationsForOwnerAsync(s.UserId, FullWorthSpaceDefaults.LegacyId, s.TxId,
            new[] { new AllocationLine(s.CatB, -50m, null) }, CancellationToken.None);
        Assert.Equal(1, await db.TransactionAllocations.CountAsync(x => x.TransactionId == s.TxId));

        var cleared = await store.ReplaceAllocationsForOwnerAsync(s.UserId, FullWorthSpaceDefaults.LegacyId, s.TxId,
            Array.Empty<AllocationLine>(), CancellationToken.None);
        Assert.Equal(AllocationResult.Updated, cleared);
        Assert.Equal(0, await db.TransactionAllocations.CountAsync(x => x.TransactionId == s.TxId));
    }

    private sealed record Seed(Guid UserId, Guid TxId, Guid CatA, Guid CatB)
    {
        public static Seed New() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        public static async Task PopulateAsync(FullWorthDbContext db, Seed s, decimal txAmount)
        {
            var connection = new BankConnection { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = $"alloc-{s.TxId:N}" };
            var account = new FinanceAccount { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, BankConnectionId = connection.Id, Provider = "test", IdentificationHash = $"alloc-{s.TxId:N}", ProviderAccountId = $"alloc-{s.TxId:N}", InstitutionName = "Bank", DisplayName = "Alloc", Currency = "EUR" };
            db.Users.Add(new FullWorthUser { Id = s.UserId, EmailNormalized = $"{s.UserId:N}@EX.COM".ToUpperInvariant(), DisplayName = "Owner", IsActive = true });
            db.BankConnections.Add(connection);
            db.Accounts.Add(account);
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, UserId = s.UserId, Role = FullWorthSpaceRoles.Member });
            db.AccountOwners.Add(new AccountOwner { AccountId = account.Id, UserId = s.UserId, OwnershipType = AccountOwnershipTypes.Owner });
            db.Categories.Add(new FinanceCategory { Id = s.CatA, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = $"a-{s.CatA:N}", Name = "A" });
            db.Categories.Add(new FinanceCategory { Id = s.CatB, FullWorthSpaceId = FullWorthSpaceDefaults.LegacyId, Key = $"b-{s.CatB:N}", Name = "B" });
            db.Transactions.Add(new FinanceTransaction { Id = s.TxId, AccountId = account.Id, ExternalKey = $"alloc-{s.TxId:N}", Amount = txAmount, Currency = "EUR", CategoryId = s.CatA, BookingDate = new DateOnly(2026, 6, 15), Status = "BOOK" });
            await db.SaveChangesAsync();
        }
    }
}
