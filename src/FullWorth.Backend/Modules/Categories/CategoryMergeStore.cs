using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

/// <summary>Woran die Quellkategorie ueberall haengt - die Zahlen, die der Benutzer vor dem Zusammenfuehren sieht.</summary>
public sealed record MergeCounts(
    int transactions, int refundCategories, int splitAllocations, int rules,
    int budgets, int contracts, int purchaseItems, int productDefaults, int activeChildren);

/// <summary>Eine Kategorie, soweit die Zusammenfuehrung sie beurteilen muss.</summary>
public sealed record MergeCandidate(Guid Id, string Name, bool IsArchived, bool IsSystem);

/// <summary>
/// Zwei Kategorien zusammenfuehren: alles, was auf die eine zeigt, zeigt danach auf die andere.
///
/// Acht Tabellen sind betroffen, und sie werden in EINER Anweisung und EINER Transaktion umgehaengt.
/// Bricht etwas ab, zeigt keine Buchung auf eine Kategorie, die gleich archiviert oder geloescht
/// wird - genau das waere sonst der Zustand, in dem Geld aus der Auswertung verschwindet.
///
/// Die Budgetzuordnungen sind der Sonderfall: zeigt ein Budget auf beide Kategorien, bleibt das
/// grosszuegigere <c>IncludeDescendants</c> stehen.
/// </summary>
public sealed class CategoryMergeStore(FullWorthDbContext db, AuditService audit)
{
    public Task<List<MergeCandidate>> FindPairAsync(
        Guid fullWorthSpaceId, Guid sourceCategoryId, Guid targetCategoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId
                            && (category.Id == sourceCategoryId || category.Id == targetCategoryId))
            .Select(category => new MergeCandidate(category.Id, category.Name, category.IsArchived, category.IsSystem))
            .ToListAsync(ct);

    /// <summary>Verfolgt - die Quelle wird gleich archiviert oder geloescht.</summary>
    public Task<FinanceCategory?> FindSourceAsync(
        Guid fullWorthSpaceId, Guid sourceCategoryId, CancellationToken ct) =>
        db.Categories.SingleOrDefaultAsync(category =>
            category.Id == sourceCategoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct);

    public Task<FinanceCategory?> FindTargetAsync(
        Guid fullWorthSpaceId, Guid targetCategoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking().SingleOrDefaultAsync(category =>
            category.Id == targetCategoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct);

    public async Task<MergeCounts> CountsAsync(Guid fullWorthSpaceId, Guid source, CancellationToken ct)
    {
        var transactions = await db.Transactions.AsNoTracking()
            .CountAsync(transaction => transaction.CategoryId == source, ct);
        var refundCategories = await db.Transactions.AsNoTracking()
            .CountAsync(transaction => transaction.RefundCategoryId == source, ct);
        var splitAllocations = await db.TransactionAllocations.AsNoTracking()
            .CountAsync(allocation => allocation.CategoryId == source, ct);
        var rules = await db.CategorizationRules.AsNoTracking()
            .CountAsync(rule => rule.FullWorthSpaceId == fullWorthSpaceId && rule.CategoryId == source, ct);
        var budgets = await db.Budgets.AsNoTracking()
            .CountAsync(budget => budget.FullWorthSpaceId == fullWorthSpaceId && budget.CategoryId == source, ct);
        var contracts = await db.Contracts.AsNoTracking()
            .CountAsync(contract => contract.FullWorthSpaceId == fullWorthSpaceId && contract.CategoryId == source, ct);
        var purchaseItems = await db.PurchaseItems.AsNoTracking()
            .CountAsync(item => item.CategoryId == source && item.Purchase.FullWorthSpaceId == fullWorthSpaceId, ct);
        var activeChildren = await db.Categories.AsNoTracking().CountAsync(category =>
            category.FullWorthSpaceId == fullWorthSpaceId && category.ParentId == source && !category.IsArchived, ct);

        // Budgetbereiche und Produktvorgaben in einer Runde: beide brauchen einen Join bzw. eine
        // Tabelle, die EF hier nur umstaendlich zaehlen wuerde.
        var connection = await RawSql.OpenAsync(db, ct);
        await using var extra = RawSql.Command(connection, """
SELECT
  (SELECT count(DISTINCT bc."BudgetId") FROM "BudgetCategories" bc
   JOIN "Budgets" b ON b."Id"=bc."BudgetId"
   WHERE b."FullWorthSpaceId"=@space AND bc."CategoryId"=@source) AS "BudgetScopes",
  (SELECT count(*) FROM "Products" p WHERE p."FullWorthSpaceId"=@space AND p."DefaultCategoryId"=@source) AS "ProductDefaults"
""", ("@space", fullWorthSpaceId), ("@source", source));
        await using var reader = await extra.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        return new MergeCounts(
            transactions, refundCategories, splitAllocations, rules,
            budgets + Convert.ToInt32(reader["BudgetScopes"]),
            contracts, purchaseItems, Convert.ToInt32(reader["ProductDefaults"]), activeChildren);
    }

    /// <summary>
    /// Haengt alles um und raeumt die Quelle weg. Ob das erlaubt ist, hat der Aufrufer entschieden.
    /// </summary>
    public async Task MergeAsync(
        Guid userId, Guid fullWorthSpaceId, FinanceCategory source, Guid targetCategoryId, bool deleteSource,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var sourceCategoryId = source.Id;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "Transactions" SET "CategoryId"={targetCategoryId}
WHERE "CategoryId"={sourceCategoryId};
UPDATE "Transactions" SET "RefundCategoryId"={targetCategoryId}
WHERE "RefundCategoryId"={sourceCategoryId};
UPDATE "TransactionAllocations" SET "CategoryId"={targetCategoryId}
WHERE "CategoryId"={sourceCategoryId};
UPDATE "CategorizationRules" SET "CategoryId"={targetCategoryId}, "UpdatedAt"={now}
WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "CategoryId"={sourceCategoryId};
UPDATE "Budgets" SET "CategoryId"={targetCategoryId}
WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "CategoryId"={sourceCategoryId};
UPDATE "Contracts" SET "CategoryId"={targetCategoryId}
WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "CategoryId"={sourceCategoryId};
UPDATE "PurchaseItems" SET "CategoryId"={targetCategoryId}
WHERE "CategoryId"={sourceCategoryId};
UPDATE "Products" SET "DefaultCategoryId"={targetCategoryId}, "UpdatedAt"={now}
WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "DefaultCategoryId"={sourceCategoryId};
""", ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "BudgetCategories" ("BudgetId","CategoryId","IncludeDescendants")
SELECT "BudgetId", {targetCategoryId}, "IncludeDescendants"
FROM "BudgetCategories" WHERE "CategoryId"={sourceCategoryId}
ON CONFLICT ("BudgetId","CategoryId") DO UPDATE
SET "IncludeDescendants"="BudgetCategories"."IncludeDescendants" OR EXCLUDED."IncludeDescendants";
DELETE FROM "BudgetCategories" WHERE "CategoryId"={sourceCategoryId};
""", ct);

        if (deleteSource) db.Categories.Remove(source);
        else source.IsArchived = true;

        audit.Record(fullWorthSpaceId, userId,
            deleteSource ? "category.merged.deleted" : "category.merged.archived",
            "FinanceCategory", sourceCategoryId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
