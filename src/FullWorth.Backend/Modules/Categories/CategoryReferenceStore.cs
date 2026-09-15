using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

/// <summary>Woran eine Kategorie ueberall haengt. Null ueberall heisst: sie loeschen tut niemandem weh.</summary>
public sealed record CategoryReferenceCounts(
    long Transactions, long Allocations, long Rules, long Budgets, long Contracts,
    long PurchaseItems, long ProductDefaults, long Refunds);

/// <summary>
/// Zaehlt, woran eine Kategorie haengt - acht Tabellen, acht Zahlen.
///
/// Das ist alles, was von CategoryErgonomicsStore uebrig ist. Dessen zwei andere Faehigkeiten,
/// Zusammenfuehren und Umsortieren, gab es zweimal: CategoryMergeStore fuehrt zusammen und zeigt
/// vorher, was sich dabei aendert, CategoryOrderService sortiert um und prueft dabei auf Zyklen und
/// doppelte Eintraege. Beide koennen mehr, beide werden benutzt. Die Zweitfassung ist am 2026-09-15
/// weggefallen.
/// </summary>
public sealed class CategoryReferenceStore(FullWorthDbContext db)
{
    public Task<bool> ExistsAsync(Guid fullWorthSpaceId, Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .AnyAsync(category => category.Id == categoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct);

    public async Task<CategoryReferenceCounts> ReferenceCountsAsync(Guid categoryId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);

        async Task<long> Count(string sql)
        {
            await using var command = RawSql.Command(connection, sql, ("@id", categoryId));
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }

        return new CategoryReferenceCounts(
            await Count("SELECT count(*) FROM \"Transactions\" WHERE \"CategoryId\"=@id"),
            await Count("SELECT count(*) FROM \"TransactionAllocations\" WHERE \"CategoryId\"=@id"),
            await Count("SELECT count(*) FROM \"CategorizationRules\" WHERE \"CategoryId\"=@id"),
            await Count("SELECT count(*) FROM \"Budgets\" WHERE \"CategoryId\"=@id")
                + await Count("SELECT count(*) FROM \"BudgetCategories\" WHERE \"CategoryId\"=@id"),
            await Count("SELECT count(*) FROM \"Contracts\" WHERE \"CategoryId\"=@id"),
            await Count("SELECT count(*) FROM \"PurchaseItems\" WHERE \"CategoryId\"=@id"),
            await Count("SELECT count(*) FROM \"Products\" WHERE \"DefaultCategoryId\"=@id"),
            await Count("SELECT count(*) FROM \"Transactions\" WHERE \"RefundCategoryId\"=@id"));
    }
}
