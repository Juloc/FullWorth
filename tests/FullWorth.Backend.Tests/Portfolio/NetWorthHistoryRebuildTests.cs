using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Parity;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class NetWorthHistoryRebuildTests
{
    [Fact]
    public async Task RebuildBackcastsCurrentBalanceAcrossHistoricalTransactions()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var depositDay = today.AddDays(-2);
        var expenseDay = today.AddDays(-1);

        db.Users.Add(new FullWorthUser
        {
            Id = userId,
            EmailNormalized = "HISTORY@EXAMPLE.COM",
            DisplayName = "History Test"
        });
        db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "History", BaseCurrency = "EUR" });
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
            Provider = "test",
            IdentificationHash = "history-account",
            ProviderAccountId = "history-account",
            InstitutionName = "Test Bank",
            DisplayName = "Checking",
            Currency = "EUR",
            IsActive = true,
            IncludeInNetWorth = true,
            Owners =
            [
                new AccountOwner
                {
                    AccountId = accountId,
                    UserId = userId,
                    OwnershipType = AccountOwnershipTypes.Owner
                }
            ]
        });
        db.BalanceSnapshots.Add(new BalanceSnapshot
        {
            AccountId = accountId,
            Amount = 900m,
            Currency = "EUR",
            BalanceType = "closingBooked",
            ReferenceDate = today,
            CapturedAt = DateTimeOffset.UtcNow
        });
        db.Transactions.AddRange(
            new FinanceTransaction
            {
                AccountId = accountId,
                ExternalKey = "deposit",
                Status = "BOOK",
                BookingDate = depositDay,
                ValueDate = depositDay,
                Amount = 1000m,
                Currency = "EUR"
            },
            new FinanceTransaction
            {
                AccountId = accountId,
                ExternalKey = "expense",
                Status = "BOOK",
                BookingDate = expenseDay,
                ValueDate = expenseDay,
                Amount = -100m,
                Currency = "EUR"
            });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.RebuildHistoryForUserAsync(spaceId, userId, null, CancellationToken.None);

        var history = await db.NetWorthSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.FullWorthSpaceId == spaceId && snapshot.UserId == userId && snapshot.Currency == "EUR")
            .OrderBy(snapshot => snapshot.Date)
            .ToListAsync();

        Assert.Equal(3, history.Count);
        Assert.Equal((depositDay, 1000m), (history[0].Date, history[0].Accounts));
        Assert.Equal((expenseDay, 900m), (history[1].Date, history[1].Accounts));
        Assert.Equal((today, 900m), (history[2].Date, history[2].Accounts));
        Assert.All(history, snapshot => Assert.Equal(snapshot.Accounts, snapshot.NetWorth));
    }

    [Fact]
    public async Task RebuildDoesNotCopyCurrentAssetValueIntoUnknownPast()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();

        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var historicalDay = today.AddDays(-1);

        db.Users.Add(new FullWorthUser
        {
            Id = userId,
            EmailNormalized = "ASSET-HISTORY@EXAMPLE.COM",
            DisplayName = "Asset History Test"
        });
        db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Asset History", BaseCurrency = "EUR" });
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
            Provider = "test",
            IdentificationHash = "asset-history-account",
            ProviderAccountId = "asset-history-account",
            InstitutionName = "Test Bank",
            DisplayName = "Checking",
            Currency = "EUR",
            Owners =
            [
                new AccountOwner
                {
                    AccountId = accountId,
                    UserId = userId,
                    OwnershipType = AccountOwnershipTypes.Owner
                }
            ]
        });
        db.BalanceSnapshots.Add(new BalanceSnapshot
        {
            AccountId = accountId,
            Amount = 100m,
            Currency = "EUR",
            BalanceType = "closingBooked",
            ReferenceDate = today,
            CapturedAt = DateTimeOffset.UtcNow
        });
        db.Transactions.Add(new FinanceTransaction
        {
            AccountId = accountId,
            ExternalKey = "history-anchor",
            Status = "BOOK",
            BookingDate = historicalDay,
            ValueDate = historicalDay,
            Amount = 100m,
            Currency = "EUR"
        });
        db.Assets.Add(new Asset
        {
            FullWorthSpaceId = spaceId,
            Name = "Current-only asset",
            CurrentValue = 500m,
            Currency = "EUR",
            IncludeInNetWorth = true,
            ValuedAt = today
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        await service.RebuildHistoryForUserAsync(spaceId, userId, null, CancellationToken.None);

        var history = await db.NetWorthSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.FullWorthSpaceId == spaceId && snapshot.UserId == userId && snapshot.Currency == "EUR")
            .OrderBy(snapshot => snapshot.Date)
            .ToListAsync();

        Assert.Equal(2, history.Count);
        Assert.Equal(0m, history[0].Assets);
        Assert.Equal(100m, history[0].NetWorth);
        Assert.Equal(500m, history[1].Assets);
        Assert.Equal(600m, history[1].NetWorth);
    }

    private static NetWorthSnapshotService CreateService(FullWorthDbContext db)
    {
        var converter = new CurrencyConverter(db);
        var investments = new InvestmentNetWorthService(db, converter);
        return new NetWorthSnapshotService(db, investments);
    }
}
