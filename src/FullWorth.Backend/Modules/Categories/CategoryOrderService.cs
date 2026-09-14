using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

public sealed record CategoryOrderApplyItem(Guid Id, Guid? ParentId, int SortOrder);
public sealed record CategoryOrderApplyWrite(IReadOnlyList<CategoryOrderApplyItem>? Items);

public enum CategoryOrderResult
{
    Applied,
    NotFound,
    Invalid,
    Conflict
}

public sealed record CategoryOrderOutcome(CategoryOrderResult Result, int Changed = 0, string? Error = null);

/// <summary>
/// Die Baumstruktur der Kategorien neu ordnen: Elternzuordnung und Sortierung in einem Rutsch.
///
/// Das lag bis 2026-09-15 vollstaendig im Handler - Laden, Pruefen, Zyklensuche, Transaktion. Die
/// Reihenfolge der Ablehnungen ist Fachlogik und keine HTTP-Uebersetzung: unbekannte Kategorie ->
/// nicht gefunden; fremdes oder archiviertes Elternteil -> ungueltig; Zyklus -> ungueltig. Der
/// Endpunkt uebersetzt das Ergebnis, mehr nicht.
/// </summary>
public sealed class CategoryOrderService(FullWorthDbContext db, AuditService audit)
{
    /// <summary>Mehr als das ordnet niemand von Hand; die Grenze schuetzt die Transaktion.</summary>
    public const int MaxItems = 500;

    public async Task<CategoryOrderOutcome> ApplyAsync(
        Guid userId, Guid fullWorthSpaceId, IReadOnlyList<CategoryOrderApplyItem> items, CancellationToken ct)
    {
        var all = await db.Categories
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToListAsync(ct);
        var byId = all.ToDictionary(category => category.Id);

        if (items.Any(item => !byId.ContainsKey(item.Id)))
            return new(CategoryOrderResult.NotFound);
        if (items.Any(item => item.ParentId.HasValue && !byId.ContainsKey(item.ParentId.Value)))
            return new(CategoryOrderResult.Invalid, Error: "A parent category belongs to another FullWorth Space or does not exist.");
        if (items.Any(item => item.ParentId.HasValue && byId[item.ParentId.Value].IsArchived))
            return new(CategoryOrderResult.Invalid, Error: "An active category cannot be moved below an archived parent.");

        var parentById = all.ToDictionary(category => category.Id, category => category.ParentId);
        foreach (var item in items) parentById[item.Id] = item.ParentId;
        if (HasCycle(parentById))
            return new(CategoryOrderResult.Invalid, Error: "Category reorder would create a parent cycle.");

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
            return new(CategoryOrderResult.Applied, changed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            return new(CategoryOrderResult.Conflict, Error: "Category order could not be saved atomically.");
        }
    }

    private static bool HasCycle(Dictionary<Guid, Guid?> parentById)
    {
        foreach (var categoryId in parentById.Keys)
        {
            var seen = new HashSet<Guid>();
            var cursor = (Guid?)categoryId;
            while (cursor.HasValue)
            {
                if (!seen.Add(cursor.Value)) return true;
                cursor = parentById.GetValueOrDefault(cursor.Value);
            }
        }
        return false;
    }
}
