using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Xunit;

namespace FullWorth.Backend.Tests.Accounts;

/// <summary>
/// Der historische Tagesendstand (#126). Die Buchungsseite zeigt ihn ueber der Liste, und er darf nicht
/// aus den gerade geladenen Zeilen entstehen - sonst zeigt jede Seite der Blaetterung eine andere Zahl.
///
/// Drei Dinge werden hier festgenagelt, weil sie in dieser Reihenfolge schon einmal falsch waren:
/// vorgemerkte Buchungen verschieben die Vergangenheit nicht, umgerechnet wird mit dem Kurs DES TAGES,
/// und ein fehlender Kurs macht den Tag unvollstaendig statt still 1:1.
/// </summary>
public sealed class DailyBalanceHistoryTests
{
    private static readonly Guid Space = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public async Task EndOfDayBalanceWalksBackThroughBookedTransactions()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var account = await SeedAsync(db, balance: 1000m, currency: "EUR");
        AddTransaction(db, account, today, -100m, "EUR");
        AddTransaction(db, account, today.AddDays(-1), -250m, "EUR");
        await db.SaveChangesAsync();

        var days = await StoreFor(db).DailyAsync(User, Space, today.AddDays(-3), today, null, null, CancellationToken.None);

        Assert.Equal(4, days.Count);
        // Heute 1000; gestern also 1100 (die 100 von heute zurueck); vorgestern 1350.
        Assert.Equal(1000m, Amount(days, today));
        Assert.Equal(1100m, Amount(days, today.AddDays(-1)));
        Assert.Equal(1350m, Amount(days, today.AddDays(-2)));
        Assert.Equal(1350m, Amount(days, today.AddDays(-3)));
        Assert.All(days, day => Assert.False(day.Incomplete));
    }

    [Fact]
    public async Task PendingTransactionsDoNotMoveThePast()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var account = await SeedAsync(db, balance: 500m, currency: "EUR");
        // Eine Vormerkung von gestern. Sie ist noch nicht gebucht, also hat sie den Stand von vorgestern
        // nicht veraendert - und der Anker ist der gebuchte Kontostand, in dem sie ebenfalls nicht steckt.
        AddTransaction(db, account, today.AddDays(-1), -80m, "EUR", status: "PDNG");
        await db.SaveChangesAsync();

        var days = await StoreFor(db).DailyAsync(User, Space, today.AddDays(-2), today, null, null, CancellationToken.None);

        Assert.All(days, day => Assert.Equal(500m, day.Amount));
    }

    [Fact]
    public async Task ForeignBalanceUsesTheRateOfThatDayAndSaysSoWhenItIsMissing()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var account = await SeedAsync(db, balance: 200m, currency: "USD");

        // Nur fuer heute gibt es einen Kurs: 1 EUR = 2 USD. Der Tag davor hat keinen - und liegt weit
        // genug zurueck, dass auch der Rueckgriff auf eine aeltere Notierung nichts findet.
        db.FxRates.Add(new FxRate { Date = today, Currency = "USD", Rate = 2m });
        await db.SaveChangesAsync();

        var days = await StoreFor(db).DailyAsync(User, Space, today.AddDays(-30), today, null, null, CancellationToken.None);

        var current = days.Single(day => day.Date == today);
        Assert.Equal(100m, current.Amount);      // 200 USD zu 2,0 sind 100 EUR
        Assert.False(current.Incomplete);

        var older = days.Single(day => day.Date == today.AddDays(-30));
        Assert.True(older.Incomplete);
        Assert.Equal(0m, older.Amount);          // kein 1:1 und keine erfundene Zahl - nur der Hinweis
    }

    [Fact]
    public async Task ScopeIsTheAccountAndNotTheOtherAccountsOfTheSpace()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var first = await SeedAsync(db, balance: 300m, currency: "EUR");
        var second = await SeedAsync(db, balance: 700m, currency: "EUR", seedSpace: false);
        await db.SaveChangesAsync();

        var store = StoreFor(db);
        var all = await store.DailyAsync(User, Space, today, today, null, null, CancellationToken.None);
        var one = await store.DailyAsync(User, Space, today, today, first, null, CancellationToken.None);
        var other = await store.DailyAsync(User, Space, today, today, second, null, CancellationToken.None);

        Assert.Equal(1000m, all.Single().Amount);
        Assert.Equal(300m, one.Single().Amount);
        Assert.Equal(700m, other.Single().Amount);
    }

    // ---- Helfer ----

    private static AccountBalanceHistoryStore StoreFor(FullWorthDbContext db) =>
        new(db, new CurrencyConverter(db));

    private static decimal Amount(IReadOnlyList<DailyBalancePoint> days, DateOnly date) =>
        days.Single(day => day.Date == date).Amount;

    private static async Task<Guid> SeedAsync(
        FullWorthDbContext db, decimal balance, string currency, bool seedSpace = true)
    {
        if (seedSpace)
        {
            db.Users.Add(new FullWorthUser { Id = User, EmailNormalized = $"{User:N}@LOCAL.TEST", DisplayName = "Test", IsActive = true });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = Space, Name = "Test", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = Space,
                UserId = User,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
        }

        var account = new FinanceAccount
        {
            FullWorthSpaceId = Space,
            Provider = "test",
            IdentificationHash = Guid.NewGuid().ToString("N"),
            ProviderAccountId = Guid.NewGuid().ToString("N"),
            InstitutionName = "Testbank",
            DisplayName = "Konto",
            Currency = currency,
            IncludeInNetWorth = true
        };
        account.Owners.Add(new AccountOwner
        {
            Account = account,
            UserId = User,
            OwnershipType = AccountOwnershipTypes.Owner
        });
        db.Accounts.Add(account);
        db.BalanceSnapshots.Add(new BalanceSnapshot
        {
            AccountId = account.Id,
            Amount = balance,
            Currency = currency,
            BalanceType = "closingBooked",
            CapturedAt = DateTimeOffset.UtcNow,
            Source = BalanceSources.Provider
        });
        await db.SaveChangesAsync();
        return account.Id;
    }

    private static void AddTransaction(
        FullWorthDbContext db, Guid accountId, DateOnly date, decimal amount, string currency,
        string status = "BOOK")
    {
        db.Transactions.Add(new FullWorth.Backend.Modules.Transactions.FinanceTransaction
        {
            AccountId = accountId,
            ExternalKey = Guid.NewGuid().ToString("N"),
            BookingDate = date,
            ValueDate = date,
            Amount = amount,
            Currency = currency,
            Status = status,
            UseForBalanceHistory = true,
            Description = "Test"
        });
    }
}
