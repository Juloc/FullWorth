using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

/// <summary>Woran eine Kategorie ueberall haengt. Null ueberall heisst: sie loeschen tut niemandem weh.</summary>
public sealed record CategoryReferenceCounts(
    long Transactions, long Allocations, long Rules, long Budgets, long Contracts,
    long PurchaseItems, long ProductDefaults, long Refunds);

/// <summary>
/// Kategorien zusammenfuehren, umsortieren und nachsehen, woran sie haengen.
///
/// Das Zusammenfuehren ist der Grund, warum das ein eigener Ort ist: es verschiebt Finanzbezuege in
/// acht Tabellen, faltet die Budgetzuordnungen zusammen und haengt die Kinder um - alles in einer
/// Transaktion. Bricht etwas ab, darf keine Buchung auf eine Kategorie zeigen, die es nicht mehr
/// gibt.
/// </summary>
public sealed class CategoryErgonomicsStore(FullWorthDbContext db, AuditService audit)
{
    public Task<bool> ExistsAsync(Guid fullWorthSpaceId, Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .AnyAsync(category => category.Id == categoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct);

    public Task<List<FinanceCategory>> ListAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Categories.Where(category => category.FullWorthSpaceId == fullWorthSpaceId).ToListAsync(ct);

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

    /// <summary>
    /// Verschiebt alle Bezuege von <paramref name="source"/> auf die Zielkategorie. Ob das fachlich
    /// erlaubt ist - insbesondere ob dabei ein Zyklus entstuende - hat der Aufrufer schon entschieden.
    /// </summary>
    public async Task MergeAsync(
        Guid userId, Guid fullWorthSpaceId, FinanceCategory source, Guid targetCategoryId,
        bool archiveSource, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = await RawSql.OpenAsync(db, ct);

        string[] updates =
        [
            "UPDATE \"Transactions\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            "UPDATE \"TransactionAllocations\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            "UPDATE \"CategorizationRules\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            "UPDATE \"Budgets\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            "UPDATE \"Contracts\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            "UPDATE \"PurchaseItems\" SET \"CategoryId\"=@target WHERE \"CategoryId\"=@source",
            // Canonical products carry the default category; the legacy per-alias category and the
            // ProductIdentities table were removed by the Products/Articles unification.
            "UPDATE \"Products\" SET \"DefaultCategoryId\"=@target WHERE \"DefaultCategoryId\"=@source",
            "UPDATE \"Transactions\" SET \"RefundCategoryId\"=@target WHERE \"RefundCategoryId\"=@source"
        ];
        foreach (var sql in updates)
        {
            await using var command = RawSql.Command(connection, sql,
                ("@source", source.Id), ("@target", targetCategoryId));
            await command.ExecuteNonQueryAsync(ct);
        }

        // Zeigt ein Budget auf beide Kategorien, bleibt das grosszuegigere IncludeDescendants stehen.
        await using (var budgetInsert = RawSql.Command(connection, """
INSERT INTO "BudgetCategories" ("BudgetId","CategoryId","IncludeDescendants")
SELECT "BudgetId",@target,"IncludeDescendants" FROM "BudgetCategories" WHERE "CategoryId"=@source
ON CONFLICT ("BudgetId","CategoryId") DO UPDATE SET "IncludeDescendants" =
  "BudgetCategories"."IncludeDescendants" OR EXCLUDED."IncludeDescendants"
""", ("@source", source.Id), ("@target", targetCategoryId)))
            await budgetInsert.ExecuteNonQueryAsync(ct);
        await using (var budgetDelete = RawSql.Command(connection,
                         "DELETE FROM \"BudgetCategories\" WHERE \"CategoryId\"=@source",
                         ("@source", source.Id)))
            await budgetDelete.ExecuteNonQueryAsync(ct);

        await using (var children = RawSql.Command(connection,
                         "UPDATE \"Categories\" SET \"ParentId\"=@target WHERE \"ParentId\"=@source AND \"Id\"<>@target",
                         ("@source", source.Id), ("@target", targetCategoryId)))
            await children.ExecuteNonQueryAsync(ct);

        if (archiveSource) source.IsArchived = true;
        audit.Record(fullWorthSpaceId, userId, "category.merged", "FinanceCategory", source.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Setzt Elternzuordnung und Sortierung. Der Aufrufer hat schon geprueft, dass kein Zyklus entsteht.</summary>
    public async Task ReorderAsync(
        Guid userId, Guid fullWorthSpaceId,
        IReadOnlyDictionary<Guid, FinanceCategory> byId, IReadOnlyList<CategoryOrderItem> items,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var item in items)
        {
            var category = byId[item.CategoryId];
            category.ParentId = item.ParentId;
            category.SortOrder = item.SortOrder;
        }
        audit.Record(fullWorthSpaceId, userId, "categories.reordered", "FullWorthSpace", fullWorthSpaceId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
