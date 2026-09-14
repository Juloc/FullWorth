using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

public sealed record CategoryMergeWrite(Guid TargetCategoryId, bool ArchiveSource = true);
public sealed record CategoryOrderItem(Guid CategoryId, Guid? ParentId, int SortOrder);
public sealed record CategoryOrderWrite(IReadOnlyList<CategoryOrderItem>? Items);

/// <summary>Kategorien zusammenfuehren, umsortieren, ihre Verweise zaehlen. Kam aus Parity.</summary>
public static class CategoryErgonomicsEndpoints
{
    public static IEndpointRouteBuilder MapCategoryErgonomicsEndpoints(this IEndpointRouteBuilder app)
    {
        var categories = app.MapGroup("/api/category-ergonomics").WithTags("Categories");
        categories.MapGet("/{categoryId:guid}/references", CategoryReferences);
        categories.MapPost("/{categoryId:guid}/merge", MergeCategory);
        categories.MapPost("/reorder", ReorderCategories);
        return app;
    }

    private static async Task<IResult> CategoryReferences(
        Guid categoryId, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await db.Categories.AsNoTracking().AnyAsync(category =>
                category.Id == categoryId && category.FullWorthSpaceId == fullWorthSpaceId, ct))
            return Results.NotFound();
        var connection = await RawSql.OpenAsync(db, ct);
        async Task<long> Count(string sql)
        {
            await using var command = RawSql.Command(connection, sql, ("@id", categoryId));
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        return Results.Ok(new
        {
            transactions = await Count("SELECT count(*) FROM \"Transactions\" WHERE \"CategoryId\"=@id"),
            allocations = await Count("SELECT count(*) FROM \"TransactionAllocations\" WHERE \"CategoryId\"=@id"),
            rules = await Count("SELECT count(*) FROM \"CategorizationRules\" WHERE \"CategoryId\"=@id"),
            budgets = await Count("SELECT count(*) FROM \"Budgets\" WHERE \"CategoryId\"=@id") +
                      await Count("SELECT count(*) FROM \"BudgetCategories\" WHERE \"CategoryId\"=@id"),
            contracts = await Count("SELECT count(*) FROM \"Contracts\" WHERE \"CategoryId\"=@id"),
            purchaseItems = await Count("SELECT count(*) FROM \"PurchaseItems\" WHERE \"CategoryId\"=@id"),
            productDefaults = await Count("SELECT count(*) FROM \"Products\" WHERE \"DefaultCategoryId\"=@id"),
            refunds = await Count("SELECT count(*) FROM \"Transactions\" WHERE \"RefundCategoryId\"=@id")
        });
    }

    private static async Task<IResult> MergeCategory(
        Guid categoryId, Guid fullWorthSpaceId, CategoryMergeWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (categoryId == request.TargetCategoryId)
            return Results.BadRequest(new { error = "Source and target must differ." });

        var allCategories = await db.Categories
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToListAsync(ct);
        var byId = allCategories.ToDictionary(category => category.Id);
        if (!byId.TryGetValue(categoryId, out var source) ||
            !byId.TryGetValue(request.TargetCategoryId, out var target)) return Results.NotFound();

        // Merging a parent into one of its descendants and then reparenting its children to that
        // descendant can create A -> B -> ... -> A. Reject before any financial references move.
        Guid? cursor = target.ParentId;
        while (cursor.HasValue)
        {
            if (cursor.Value == source.Id)
                return Results.BadRequest(new { error = "Target category is a descendant of the source. Reparent it first." });
            cursor = byId.GetValueOrDefault(cursor.Value)?.ParentId;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = await RawSql.OpenAsync(db, ct);
        var updates = new[]
        {
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
        };
        foreach (var sql in updates)
        {
            await using var command = RawSql.Command(connection, sql,
                ("@source", categoryId), ("@target", request.TargetCategoryId));
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var budgetInsert = RawSql.Command(connection, """
INSERT INTO "BudgetCategories" ("BudgetId","CategoryId","IncludeDescendants")
SELECT "BudgetId",@target,"IncludeDescendants" FROM "BudgetCategories" WHERE "CategoryId"=@source
ON CONFLICT ("BudgetId","CategoryId") DO UPDATE SET "IncludeDescendants" =
  "BudgetCategories"."IncludeDescendants" OR EXCLUDED."IncludeDescendants"
""", ("@source", categoryId), ("@target", request.TargetCategoryId)))
            await budgetInsert.ExecuteNonQueryAsync(ct);
        await using (var budgetDelete = RawSql.Command(connection,
                         "DELETE FROM \"BudgetCategories\" WHERE \"CategoryId\"=@source",
                         ("@source", categoryId)))
            await budgetDelete.ExecuteNonQueryAsync(ct);

        await using (var children = RawSql.Command(connection,
                         "UPDATE \"Categories\" SET \"ParentId\"=@target WHERE \"ParentId\"=@source AND \"Id\"<>@target",
                         ("@source", categoryId), ("@target", request.TargetCategoryId)))
            await children.ExecuteNonQueryAsync(ct);

        if (request.ArchiveSource) source.IsArchived = true;
        audit.Record(fullWorthSpaceId, userId, "category.merged", "FinanceCategory", categoryId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(new
        {
            sourceCategoryId = categoryId,
            targetCategoryId = request.TargetCategoryId,
            archived = source.IsArchived
        });
    }

    private static async Task<IResult> ReorderCategories(
        Guid fullWorthSpaceId, CategoryOrderWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var items = (request.Items ?? []).DistinctBy(item => item.CategoryId).ToArray();
        if (items.Length == 0 || items.Length > 1000)
            return Results.BadRequest(new { error = "Invalid category order request." });
        var all = await db.Categories.Where(category => category.FullWorthSpaceId == fullWorthSpaceId).ToListAsync(ct);
        var byId = all.ToDictionary(category => category.Id);
        if (items.Any(item => !byId.ContainsKey(item.CategoryId) ||
                              item.ParentId.HasValue && !byId.ContainsKey(item.ParentId.Value)))
            return Results.BadRequest(new { error = "Category does not belong to this FullWorth Space." });

        var proposedParents = all.ToDictionary(category => category.Id, category => category.ParentId);
        foreach (var item in items) proposedParents[item.CategoryId] = item.ParentId;
        foreach (var categoryId in proposedParents.Keys)
        {
            var visited = new HashSet<Guid>();
            Guid? cursor = categoryId;
            while (cursor.HasValue)
            {
                if (!visited.Add(cursor.Value))
                    return Results.BadRequest(new { error = "Category hierarchy would contain a cycle." });
                cursor = proposedParents.GetValueOrDefault(cursor.Value);
            }
        }

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
        return Results.NoContent();
    }

}
