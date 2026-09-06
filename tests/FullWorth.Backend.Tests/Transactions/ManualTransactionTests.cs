using System.Text.Json;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Transactions;

// Manual booking (UI_UX_SPEC §9.4): an account OWNER may hand-book income/expense transactions on a
// connection-less "manual" account, and delete them again. Imported bank transactions are never
// created or deleted this way.
public sealed class ManualTransactionTests
{
    private static readonly Guid Space = FullWorthSpaceDefaults.LegacyId;

    [Fact]
    public async Task Books_expense_and_income_with_correct_sign_and_manual_key()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        var (expenseResult, expenseId) = await store.CreateManualForOwnerAsync(s.Owner, Space,
            new CreateTransactionRequest(s.ManualAccount, 12.50m, "expense", new DateOnly(2026, 8, 20), null, "Bäckerei", s.Category, "Frühstück"), CancellationToken.None);
        Assert.Equal(TransactionCreateResult.Created, expenseResult);

        var expense = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == expenseId);
        Assert.Equal(-12.50m, expense.Amount);                 // expense is negative
        Assert.Equal("EUR", expense.Currency);                 // defaults to the account currency
        Assert.StartsWith("manual:", expense.ExternalKey);
        Assert.Equal("Bäckerei", expense.Counterparty);
        Assert.Equal(s.Category, expense.CategoryId);
        Assert.Equal("manual", expense.CategorizationSource);
        Assert.Equal(new DateOnly(2026, 8, 20), expense.BookingDate);

        var (incomeResult, incomeId) = await store.CreateManualForOwnerAsync(s.Owner, Space,
            new CreateTransactionRequest(s.ManualAccount, 100m, "income", null, null, "Gehalt", null, null), CancellationToken.None);
        Assert.Equal(TransactionCreateResult.Created, incomeResult);
        var income = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == incomeId);
        Assert.Equal(100m, income.Amount);                     // income is positive
        Assert.Equal("none", income.CategorizationSource);     // no category chosen
    }

    [Fact]
    public async Task Transaction_queries_expose_category_name_and_icon_key()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();

        var category = await db.Categories.SingleAsync(x => x.Id == s.Category);
        category.Icon = "🛒";
        var transaction = await db.Transactions.SingleAsync(x => x.Id == s.ImportedTx);
        transaction.CategoryId = s.Category;
        await db.SaveChangesAsync();

        var store = new TransactionStore(db);
        var query = new TransactionQuery(null, null, null, null, null, null, null, null, null, null, null, 20);

        var listJson = JsonSerializer.Serialize(
            await store.SearchForUserAsync(s.Owner, Space, query, CancellationToken.None),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var listDoc = JsonDocument.Parse(listJson);
        var item = listDoc.RootElement.GetProperty("items")[0];
        Assert.Equal("Lebensmittel", item.GetProperty("categoryName").GetString());
        Assert.Equal("🛒", item.GetProperty("categoryIconKey").GetString());

        var detailJson = JsonSerializer.Serialize(
            await store.GetForUserAsync(s.Owner, Space, s.ImportedTx, CancellationToken.None),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var detailDoc = JsonDocument.Parse(detailJson);
        var detailTx = detailDoc.RootElement.GetProperty("transaction");
        Assert.Equal("Lebensmittel", detailTx.GetProperty("categoryName").GetString());
        Assert.Equal("🛒", detailTx.GetProperty("categoryIconKey").GetString());
    }

    [Fact]
    public async Task Allows_manual_correction_on_a_synced_account()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        var (result, id) = await store.CreateManualForOwnerAsync(s.Owner, Space,
            new CreateTransactionRequest(s.BankAccount, 5m, "expense", new DateOnly(2022, 3, 4), null, "Fehlende Buchung", null, null), CancellationToken.None);
        Assert.Equal(TransactionCreateResult.Created, result);

        var correction = await db.Transactions.AsNoTracking().SingleAsync(tx => tx.Id == id);
        Assert.StartsWith("manual:", correction.ExternalKey);
        Assert.Equal(-5m, correction.Amount);
        Assert.True(correction.UseForBalanceHistory);
        Assert.Equal(2, await db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Rejects_unknown_category_foreign_account_and_non_owner()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        var (badCategory, _) = await store.CreateManualForOwnerAsync(s.Owner, Space,
            new CreateTransactionRequest(s.ManualAccount, 5m, "expense", null, null, "X", Guid.NewGuid(), null), CancellationToken.None);
        Assert.Equal(TransactionCreateResult.InvalidCategory, badCategory);

        var (unknownAccount, _) = await store.CreateManualForOwnerAsync(s.Owner, Space,
            new CreateTransactionRequest(Guid.NewGuid(), 5m, "expense", null, null, "X", null, null), CancellationToken.None);
        Assert.Equal(TransactionCreateResult.NotFound, unknownAccount);

        // A viewer holds an ownership row but is not an OWNER -> forbidden, not silently allowed.
        var (viewer, _) = await store.CreateManualForOwnerAsync(s.Viewer, Space,
            new CreateTransactionRequest(s.ManualAccount, 5m, "expense", null, null, "X", null, null), CancellationToken.None);
        Assert.Equal(TransactionCreateResult.Forbidden, viewer);
    }

    [Theory]
    [InlineData(0, "expense", "Legit")]      // zero amount
    [InlineData(5, "sideways", "Legit")]     // invalid direction
    [InlineData(5, "expense", "   ")]        // blank description
    public async Task Rejects_invalid_input_with_argument_exception(decimal amount, string direction, string description)
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateManualForOwnerAsync(s.Owner, Space,
            new CreateTransactionRequest(s.ManualAccount, amount, direction, null, null, description, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Deletes_a_manual_transaction_and_cascades_its_split_lines()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        Guid id;
        await using (var db = database.CreateContext())
        {
            var store = new TransactionStore(db);
            (_, id) = await store.CreateManualForOwnerAsync(s.Owner, Space,
                new CreateTransactionRequest(s.ManualAccount, 40m, "expense", null, null, "Splitme", null, null), CancellationToken.None);
            db.TransactionAllocations.Add(new TransactionAllocation { TransactionId = id, Amount = -40m, CategoryId = s.Category });
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            var store = new TransactionStore(db);
            Assert.Equal(TransactionDeleteResult.Deleted, await store.DeleteManualForOwnerAsync(s.Owner, Space, id, CancellationToken.None));
        }

        await using (var db = database.CreateContext())
        {
            Assert.False(await db.Transactions.AnyAsync(x => x.Id == id));
            Assert.False(await db.TransactionAllocations.AnyAsync(x => x.TransactionId == id));
        }
    }

    [Fact]
    public async Task Cannot_delete_an_imported_bank_transaction()
    {
        await using var database = await SqliteFullWorthDatabase.CreateAsync();
        var s = await SeedAsync(database);
        await using var db = database.CreateContext();
        var store = new TransactionStore(db);

        Assert.Equal(TransactionDeleteResult.NotManual, await store.DeleteManualForOwnerAsync(s.Owner, Space, s.ImportedTx, CancellationToken.None));
        Assert.True(await db.Transactions.AnyAsync(x => x.Id == s.ImportedTx));
    }

    private sealed record Seed(Guid Owner, Guid Viewer, Guid ManualAccount, Guid BankAccount, Guid Category, Guid ImportedTx);

    private static async Task<Seed> SeedAsync(SqliteFullWorthDatabase database)
    {
        var owner = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        var manual = new FinanceAccount { FullWorthSpaceId = Space, BankConnectionId = null, Provider = "manual", IdentificationHash = $"m-{owner:N}", ProviderAccountId = $"m-{owner:N}", InstitutionName = "Bargeld", DisplayName = "Bargeld", Currency = "EUR" };
        var connection = new BankConnection { FullWorthSpaceId = Space, Provider = "test", InstitutionName = "Bank", Country = "DE", ProviderSessionId = $"s-{owner:N}" };
        var bank = new FinanceAccount { FullWorthSpaceId = Space, BankConnectionId = connection.Id, Provider = "test", IdentificationHash = $"b-{owner:N}", ProviderAccountId = $"b-{owner:N}", InstitutionName = "Bank", DisplayName = "Giro", Currency = "EUR" };
        var category = new FinanceCategory { FullWorthSpaceId = Space, Key = "groceries", Name = "Lebensmittel" };
        var importedTx = Guid.NewGuid();

        await using var db = database.CreateContext();
        db.Users.Add(new FullWorthUser { Id = owner, EmailNormalized = $"{owner:N}@EX.COM", DisplayName = "Owner", IsActive = true });
        db.Users.Add(new FullWorthUser { Id = viewer, EmailNormalized = $"{viewer:N}@EX.COM", DisplayName = "Viewer", IsActive = true });
        db.BankConnections.Add(connection);
        db.Accounts.AddRange(manual, bank);
        db.Categories.Add(category);
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = Space, UserId = owner, Role = FullWorthSpaceRoles.Member });
        db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember { FullWorthSpaceId = Space, UserId = viewer, Role = FullWorthSpaceRoles.Member });
        db.AccountOwners.Add(new AccountOwner { AccountId = manual.Id, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
        db.AccountOwners.Add(new AccountOwner { AccountId = manual.Id, UserId = viewer, OwnershipType = AccountOwnershipTypes.Viewer });
        db.AccountOwners.Add(new AccountOwner { AccountId = bank.Id, UserId = owner, OwnershipType = AccountOwnershipTypes.Owner });
        db.Transactions.Add(new FinanceTransaction { Id = importedTx, AccountId = bank.Id, ExternalKey = "prov-123", Amount = -9m, Currency = "EUR", BookingDate = new DateOnly(2026, 6, 1), Status = "BOOK" });
        await db.SaveChangesAsync();

        return new Seed(owner, viewer, manual.Id, bank.Id, category.Id, importedTx);
    }
}
