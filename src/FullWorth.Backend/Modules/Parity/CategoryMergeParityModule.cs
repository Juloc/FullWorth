using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record CategoryMergeApplyWrite(Guid TargetCategoryId, bool DeleteSource = false);

public static class CategoryMergeParityEndpoints
{
    public static IEndpointRouteBuilder MapCategoryMergeParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/category-merge").WithTags("Categories");
        group.MapGet("/{sourceCategoryId:guid}/preview", Preview);
        group.MapPost("/{sourceCategoryId:guid}", Apply);
        return app;
    }

    private static async Task<IResult> Preview(
        Guid sourceCategoryId,
        Guid targetCategoryId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var categories = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId &&
                               (category.Id == sourceCategoryId || category.Id == targetCategoryId))
            .Select(category => new { category.Id, category.Name, category.IsArchived, category.IsSystem })
            .ToListAsync(ct);
        var source = categories.SingleOrDefault(category => category.Id == sourceCategoryId);
        var target = categories.SingleOrDefault(category => category.Id == targetCategoryId);
        if (source is null || target is null) return Results.NotFound();
        if (sourceCategoryId == targetCategoryId)
            return Results.BadRequest(new { error = "Source and target category must differ." });
        if (target.IsArchived)
            return Results.BadRequest(new { error = "Target category must be active." });

        var counts = await Counts(db, fullWorthSpaceId, sourceCategoryId, ct);
        return Results.Ok(new
        {
            source = new { source.Id, source.Name, source.IsArchived, source.IsSystem },
            target = new { target.Id, target.Name },
            counts.transactions,
            counts.refundCategories,
            counts.splitAllocations,
            counts.rules,
            counts.budgets,
            counts.contracts,
            counts.purchaseItems,
            counts.productDefaults,
            counts.activeChildren,
            canApply = counts.activeChildren == 0,
            canDeleteSource = counts.activeChildren == 0 && !source.IsSystem
        });
    }

    private static async Task<IResult> Apply(
        Guid sourceCategoryId,
        Guid fullWorthSpaceId,
        CategoryMergeApplyWrite request,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        AuditService audit,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (sourceCategoryId == request.TargetCategoryId)
            return Results.BadRequest(new { error = "Source and target category must differ." });

        var source = await db.Categories.SingleOrDefaultAsync(category =>
            category.Id == sourceCategoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct);
        var target = await db.Categories.AsNoTracking().SingleOrDefaultAsync(category =>
            category.Id == request.TargetCategoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (source is null || target is null) return Results.NotFound();
        if (target.IsArchived)
            return Results.BadRequest(new { error = "Target category must be active." });
        if (request.DeleteSource && source.IsSystem)
            return Results.BadRequest(new { error = "Built-in categories can be archived after reassignment but not permanently deleted." });

        var counts = await Counts(db, fullWorthSpaceId, sourceCategoryId, ct);
        if (counts.activeChildren > 0)
            return Results.Conflict(new { error = "Move, merge or archive child categories first.", activeChildren = counts.activeChildren });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "Transactions" SET "CategoryId"={request.TargetCategoryId}
WHERE "CategoryId"={sourceCategoryId};
UPDATE "Transactions" SET "RefundCategoryId"={request.TargetCategoryId}
WHERE "RefundCategoryId"={sourceCategoryId};
UPDATE "TransactionAllocations" SET "CategoryId"={request.TargetCategoryId}
WHERE "CategoryId"={sourceCategoryId};
UPDATE "CategorizationRules" SET "CategoryId"={request.TargetCategoryId}, "UpdatedAt"={DateTimeOffset.UtcNow}
WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "CategoryId"={sourceCategoryId};
UPDATE "Budgets" SET "CategoryId"={request.TargetCategoryId}
WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "CategoryId"={sourceCategoryId};
UPDATE "Contracts" SET "CategoryId"={request.TargetCategoryId}
WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "CategoryId"={sourceCategoryId};
UPDATE "PurchaseItems" SET "CategoryId"={request.TargetCategoryId}
WHERE "CategoryId"={sourceCategoryId};
UPDATE "Products" SET "DefaultCategoryId"={request.TargetCategoryId}, "UpdatedAt"={DateTimeOffset.UtcNow}
WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "DefaultCategoryId"={sourceCategoryId};
""", ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "BudgetCategories" ("BudgetId","CategoryId","IncludeDescendants")
SELECT "BudgetId", {request.TargetCategoryId}, "IncludeDescendants"
FROM "BudgetCategories" WHERE "CategoryId"={sourceCategoryId}
ON CONFLICT ("BudgetId","CategoryId") DO UPDATE
SET "IncludeDescendants"="BudgetCategories"."IncludeDescendants" OR EXCLUDED."IncludeDescendants";
DELETE FROM "BudgetCategories" WHERE "CategoryId"={sourceCategoryId};
""", ct);

            if (request.DeleteSource)
                db.Categories.Remove(source);
            else
                source.IsArchived = true;

            audit.Record(fullWorthSpaceId, userId,
                request.DeleteSource ? "category.merged.deleted" : "category.merged.archived",
                "FinanceCategory", sourceCategoryId);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Ok(new
            {
                sourceCategoryId,
                targetCategoryId = request.TargetCategoryId,
                sourceDeleted = request.DeleteSource,
                reassigned = new
                {
                    counts.transactions,
                    counts.refundCategories,
                    counts.splitAllocations,
                    counts.rules,
                    counts.budgets,
                    counts.contracts,
                    counts.purchaseItems,
                    counts.productDefaults
                }
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new
            {
                error = "Category merge could not be completed atomically. No category references were changed."
            });
        }
    }

    private static async Task<bool> CanManage(FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct) &&
        await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct);

    private static async Task<MergeCounts> Counts(FullWorthDbContext db, Guid fullWorthSpaceId, Guid source, CancellationToken ct)
    {
        var transactions = await db.Transactions.AsNoTracking().CountAsync(transaction => transaction.CategoryId == source, ct);
        var refundCategories = await db.Transactions.AsNoTracking().CountAsync(transaction => transaction.RefundCategoryId == source, ct);
        var splitAllocations = await db.TransactionAllocations.AsNoTracking().CountAsync(allocation => allocation.CategoryId == source, ct);
        var rules = await db.CategorizationRules.AsNoTracking().CountAsync(rule => rule.FullWorthSpaceId == fullWorthSpaceId && rule.CategoryId == source, ct);
        var budgets = await db.Budgets.AsNoTracking().CountAsync(budget => budget.FullWorthSpaceId == fullWorthSpaceId && budget.CategoryId == source, ct);
        var contracts = await db.Contracts.AsNoTracking().CountAsync(contract => contract.FullWorthSpaceId == fullWorthSpaceId && contract.CategoryId == source, ct);
        var purchaseItems = await db.PurchaseItems.AsNoTracking().CountAsync(item => item.CategoryId == source && item.Purchase.FullWorthSpaceId == fullWorthSpaceId, ct);
        var activeChildren = await db.Categories.AsNoTracking().CountAsync(category =>
            category.FullWorthSpaceId == fullWorthSpaceId && category.ParentId == source && !category.IsArchived, ct);

        var connection = await ParitySql.OpenAsync(db, ct);
        await using var extra = ParitySql.Command(connection, """
SELECT
  (SELECT count(DISTINCT bc."BudgetId") FROM "BudgetCategories" bc
   JOIN "Budgets" b ON b."Id"=bc."BudgetId"
   WHERE b."FullWorthSpaceId"=@space AND bc."CategoryId"=@source) AS "BudgetScopes",
  (SELECT count(*) FROM "Products" p WHERE p."FullWorthSpaceId"=@space AND p."DefaultCategoryId"=@source) AS "ProductDefaults"
""", ("@space", fullWorthSpaceId), ("@source", source));
        await using var reader = await extra.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var budgetScopes = Convert.ToInt32(reader["BudgetScopes"]);
        var productDefaults = Convert.ToInt32(reader["ProductDefaults"]);

        return new(transactions, refundCategories, splitAllocations, rules,
            budgets + budgetScopes, contracts, purchaseItems, productDefaults, activeChildren);
    }

    private sealed record MergeCounts(
        int transactions,
        int refundCategories,
        int splitAllocations,
        int rules,
        int budgets,
        int contracts,
        int purchaseItems,
        int productDefaults,
        int activeChildren);
}
