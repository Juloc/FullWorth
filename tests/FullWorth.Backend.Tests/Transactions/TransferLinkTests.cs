using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Transactions;

// Manual transfer linking (UI_UX_SPEC §9.7 Flow D): a user can link two transactions as a transfer
// pair (or confirm an auto-detected one — same code path), and unlink a pair back to two ordinary
// transactions. Unlike auto-detection, manual linking has no date-window restriction.
public sealed class TransferLinkTests
{
    private static readonly Guid Space = FullWorthSpaceDefaults.LegacyId;

    [Fact]
    public async Task LinksTwoOppositeTransactionsAcrossAccountsRegardlessOfDateGap()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        // 10 days apart — well outside the 3-day auto-detection window, which manual linking ignores.
        var result = await store.LinkTransferForOwnerAsync(s.Owner, Space, s.Out1, s.In1, CancellationToken.None);
        Assert.Equal(TransferLinkResult.Linked, result);

        var rows = await db.Transactions.AsNoTracking().Where(x => x.Id == s.Out1 || x.Id == s.In1).ToListAsync();
        Assert.All(rows, x => Assert.True(x.IsTransfer));
        Assert.Equal(rows[0].TransferGroupId, rows[1].TransferGroupId);
        Assert.NotNull(rows[0].TransferGroupId);
    }

    [Fact]
    public async Task RejectsSameAccountMismatchedCurrencyOrNonOppositeAmount()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        Assert.Equal(TransferLinkResult.Invalid, await store.LinkTransferForOwnerAsync(s.Owner, Space, s.Out1, s.Out1, CancellationToken.None));
        Assert.Equal(TransferLinkResult.Invalid, await store.LinkTransferForOwnerAsync(s.Owner, Space, s.Out1, s.SameAccountCredit, CancellationToken.None)); // same account
        Assert.Equal(TransferLinkResult.Invalid, await store.LinkTransferForOwnerAsync(s.Owner, Space, s.Out1, s.UsdIn, CancellationToken.None));            // different currency
        Assert.Equal(TransferLinkResult.Invalid, await store.LinkTransferForOwnerAsync(s.Owner, Space, s.Out1, s.WrongAmountIn, CancellationToken.None));   // not exactly opposite
    }

    [Fact]
    public async Task CannotLinkATransactionAlreadyInAGroup()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        Assert.Equal(TransferLinkResult.Linked, await store.LinkTransferForOwnerAsync(s.Owner, Space, s.Out1, s.In1, CancellationToken.None));
        // A third leg trying to pair with an already-linked transaction is rejected, not silently regrouped.
        Assert.Equal(TransferLinkResult.Invalid, await store.LinkTransferForOwnerAsync(s.Owner, Space, s.Out1, s.In2, CancellationToken.None));
    }

    [Fact]
    public async Task UnlinkDemotesBothLegs()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);
        await store.LinkTransferForOwnerAsync(s.Owner, Space, s.Out1, s.In1, CancellationToken.None);

        Assert.Equal(TransferUnlinkResult.Unlinked, await store.UnlinkTransferForOwnerAsync(s.Owner, Space, s.Out1, CancellationToken.None));

        var rows = await db.Transactions.AsNoTracking().Where(x => x.Id == s.Out1 || x.Id == s.In1).ToListAsync();
        Assert.All(rows, x => { Assert.False(x.IsTransfer); Assert.Null(x.TransferGroupId); });
    }

    [Fact]
    public async Task UnlinkingAnUnlinkedTransactionReportsNotLinked()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        Assert.Equal(TransferUnlinkResult.NotLinked, await store.UnlinkTransferForOwnerAsync(s.Owner, Space, s.Out1, CancellationToken.None));
    }

    [Fact]
    public async Task DemotingViaClassificationReleasesTheCounterpartToo()
    {
        // Regression: unchecking "mark as transfer" on ONE leg of a linked pair used to leave the
        // other leg still flagged IsTransfer=true with a now-orphaned TransferGroupId.
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);
        await store.LinkTransferForOwnerAsync(s.Owner, Space, s.Out1, s.In1, CancellationToken.None);

        var result = await store.ClassifyForOwnerAsync(s.Owner, Space, s.Out1, new TransactionClassification(null, false, false), CancellationToken.None);
        Assert.Equal(TransactionClassificationResult.Updated, result);

        var rows = await db.Transactions.AsNoTracking().Where(x => x.Id == s.Out1 || x.Id == s.In1).ToListAsync();
        Assert.All(rows, x => { Assert.False(x.IsTransfer); Assert.Null(x.TransferGroupId); });
    }

    [Fact]
    public async Task MarkingTransferAloneWithoutAPickedCounterpartSetsNoGroup()
    {
        // A bare "mark as transfer" checkbox with no chosen counterpart is a valid soft classification
        // (excluded from statistics) — it must NOT fabricate a group of one.
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        await store.ClassifyForOwnerAsync(s.Owner, Space, s.Out1, new TransactionClassification(null, false, true), CancellationToken.None);

        var row = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == s.Out1);
        Assert.True(row.IsTransfer);
        Assert.Null(row.TransferGroupId);
    }

    private sealed record Seed(Guid Owner, Guid Out1, Guid In1, Guid In2, Guid SameAccountCredit, Guid UsdIn, Guid WrongAmountIn);

    private static async Task<Seed> SeedAsync(SqliteFullWorthDatabase database)
    {
        var owner = Guid.NewGuid();
        var connectionA = new BankConnection { FullWorthSpaceId = Space, Provider = "test", InstitutionName = "Giro", Country = "DE", ProviderSessionId = $"a-{owner:N}" };
        var connectionB = new BankConnection { FullWorthSpaceId = Space, Provider = "test", InstitutionName = "Extra", Country = "DE", ProviderSessionId = $"b-{owner:N}" };
        var accountA = new FinanceAccount { FullWorthSpaceId = Space, BankConnectionId = connectionA.Id, Provider = "test", IdentificationHash = $"a-{owner:N}", ProviderAccountId = $"a-{owner:N}", InstitutionName = "Giro", DisplayName = "Giro", Currency = "EUR" };
        var accountB = new FinanceAccount { FullWorthSpaceId = Space, BankConnectionId = connectionB.Id, Provider = "test", IdentificationHash = $"b-{owner:N}", ProviderAccountId = $"b-{owner:N}", InstitutionName = "Extra", DisplayName = "Extra", Currency = "EUR" };
        var s = new Seed(owner, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await using var db = database.CreateContext();
        db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = $"{owner:N}@EX.COM", DisplayName = "Owner", IsActive = true });
        db.BankConnections.AddRange(connectionA, connectionB);
        db.Accounts.AddRange(accountA, accountB);
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = Space, UserId = owner, Role = FullWorthSpaceRoles.Member });
        db.AccountOwners.Add(new AccountOwner { AccountId = accountA.Id, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
        db.AccountOwners.Add(new AccountOwner { AccountId = accountB.Id, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });

        void Tx(Guid id, Guid accountId, decimal amount, DateOnly date) =>
            db.Transactions.Add(new FinanceTransaction { Id = id, AccountId = accountId, ExternalKey = $"tx-{id:N}", Amount = amount, Currency = "EUR", BookingDate = date, Status = "BOOK" });
        Tx(s.Out1, accountA.Id, -600m, new DateOnly(2026, 6, 1));
        Tx(s.In1, accountB.Id, 600m, new DateOnly(2026, 6, 11));            // 10 days apart, outside the auto window
        Tx(s.In2, accountB.Id, 600m, new DateOnly(2026, 6, 12));
        Tx(s.SameAccountCredit, accountA.Id, 600m, new DateOnly(2026, 6, 1));
        db.Transactions.Add(new FinanceTransaction { Id = s.UsdIn, AccountId = accountB.Id, ExternalKey = $"tx-{s.UsdIn:N}", Amount = 600m, Currency = "USD", BookingDate = new DateOnly(2026, 6, 1), Status = "BOOK" });
        Tx(s.WrongAmountIn, accountB.Id, 500m, new DateOnly(2026, 6, 1));
        await db.SaveChangesAsync();

        return s;
    }
}
