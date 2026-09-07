using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class FinancialSignalInvalidationTests
{
    [Fact]
    public void Category_only_transaction_change_invalidates_signals_but_not_net_worth()
    {
        using var harness = Harness.Create();
        var transaction = new FinanceTransaction
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            ExternalKey = "test",
            Amount = -10m,
            Currency = "EUR",
            CategoryId = Guid.NewGuid()
        };
        harness.Db.Transactions.Attach(transaction);
        transaction.CategoryId = Guid.NewGuid();

        var changes = Capture(harness.Db);

        Assert.True(changes.SignalsAffected);
        Assert.False(changes.NetWorthAffected);
        Assert.Contains(transaction.AccountId, changes.AccountIds);
    }

    [Fact]
    public void Amount_change_invalidates_both_signals_and_net_worth()
    {
        using var harness = Harness.Create();
        var transaction = new FinanceTransaction
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            ExternalKey = "test",
            Amount = -10m,
            Currency = "EUR"
        };
        harness.Db.Transactions.Attach(transaction);
        transaction.Amount = -20m;

        var changes = Capture(harness.Db);

        Assert.True(changes.SignalsAffected);
        Assert.True(changes.NetWorthAffected);
    }

    [Fact]
    public void Budget_change_invalidates_signals_without_rebuilding_net_worth()
    {
        using var harness = Harness.Create();
        var budget = new Budget
        {
            Id = Guid.NewGuid(),
            FullWorthSpaceId = Guid.NewGuid(),
            Name = "Food",
            Amount = 400m,
            Currency = "EUR",
            Period = "monthly"
        };
        harness.Db.Set<Budget>().Attach(budget);
        budget.Amount = 450m;

        var changes = Capture(harness.Db);

        Assert.True(changes.SignalsAffected);
        Assert.False(changes.NetWorthAffected);
        Assert.Contains(budget.FullWorthSpaceId, changes.FullWorthSpaceIds);
    }

    private static FinancialDataChangeSet Capture(FullWorthDbContext db)
    {
        var state = new FinancialDataConsistencyState();
        state.Capture(db);
        Assert.True(state.TryTake(db, out var changes));
        return changes;
    }

    private sealed class Harness : IDisposable
    {
        private Harness(SqliteConnection connection, FullWorthDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        private SqliteConnection Connection { get; }
        public FullWorthDbContext Db { get; }

        public static Harness Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<FullWorthDbContext>()
                .UseSqlite(connection)
                .Options;
            return new Harness(connection, new FullWorthDbContext(options));
        }

        public void Dispose()
        {
            Db.Dispose();
            Connection.Dispose();
        }
    }
}
