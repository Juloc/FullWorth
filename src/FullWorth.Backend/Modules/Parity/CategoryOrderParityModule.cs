using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record CategoryOrderApplyItem(Guid Id, Guid? ParentId, int SortOrder);
public sealed record CategoryOrderApplyWrite(IReadOnlyList<CategoryOrderApplyItem>? Items);

public static class CategoryOrderParityEndpoints
{
    public static IEndpointRouteBuilder MapCategoryOrderParityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/category-order", Apply).WithTags("Categories");
        return app;
    }

    private static async Task<IResult> Apply(
        Guid fullWorthSpaceId,
        CategoryOrderApplyWrite request,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        AuditService audit,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var items = (request.Items ?? []).ToArray();
        if (items.Length == 0) return Results.Ok(new { changed = 0 });
        if (items.Length > 500) return Results.BadRequest(new { error = "At most 500 categories can be reordered at once." });
        if (items.Select(item => item.Id).Distinct().Count() != items.Length)
            return Results.BadRequest(new { error = "Each category can appear only once." });
        if (items.Any(item => item.SortOrder is < 0 or > 1_000_000))
            return Results.BadRequest(new { error = "Category sort order is out of range." });
        if (items.Any(item => item.ParentId == item.Id))
            return Results.BadRequest(new { error = "A category cannot be its own parent." });

        var all = await db.Categories.Where(category => category.FullWorthSpaceId == fullWorthSpaceId).ToListAsync(ct);
        var byId = all.ToDictionary(category => category.Id);
        if (items.Any(item => !byId.ContainsKey(item.Id))) return Results.NotFound();
        if (items.Any(item => item.ParentId.HasValue && !byId.ContainsKey(item.ParentId.Value)))
            return Results.BadRequest(new { error = "A parent category belongs to another FullWorth Space or does not exist." });
        if (items.Any(item => item.ParentId.HasValue && byId[item.ParentId.Value].IsArchived))
            return Results.BadRequest(new { error = "An active category cannot be moved below an archived parent." });

        var parentById = all.ToDictionary(category => category.Id, category => category.ParentId);
        foreach (var item in items) parentById[item.Id] = item.ParentId;
        foreach (var categoryId in parentById.Keys)
        {
            var seen = new HashSet<Guid>();
            var cursor = (Guid?)categoryId;
            while (cursor.HasValue)
            {
                if (!seen.Add(cursor.Value))
                    return Results.BadRequest(new { error = "Category reorder would create a parent cycle." });
                cursor = parentById.GetValueOrDefault(cursor.Value);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var changed = 0;
            foreach (var item in items)
            {
                var category = byId[item.Id];
                if (category.ParentId == item.ParentId && category.SortOrder == item.SortOrder) continue;
                category.ParentId = item.ParentId;
                category.SortOrder = item.SortOrder;
                changed++;
            }
            if (changed > 0)
            {
                audit.Record(fullWorthSpaceId, userId, "category.order.updated", "FinanceCategory");
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return Results.Ok(new { changed });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            return Results.Conflict(new { error = "Category order could not be saved atomically." });
        }
    }
}
