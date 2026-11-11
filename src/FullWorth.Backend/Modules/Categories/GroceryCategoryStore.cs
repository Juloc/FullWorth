using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

public sealed record GroceryCategorySetup(int Created, int Adopted, int Total);

/// <summary>
/// Legt den Baum der Supermarkt-Kategorien in einem bestehenden Space an. Der Start saet nur, wo noch nie
/// gesaet wurde (siehe FullWorthSeeder.SeedAsync) - wer schon Kategorien hat, bekommt neue Standardkategorien
/// deshalb nur auf Wunsch.
///
/// Was der Benutzer schon angelegt hat, bleibt seins: eine Kategorie mit demselben Namen ("Alkohol",
/// "Süßigkeiten", "Schokolade", "Spirituosen") wird nicht doppelt angelegt, sondern an die richtige Stelle
/// gehaengt und bekommt den Standardschluessel, damit die Zuordnung der Positionen sie findet.
/// </summary>
public sealed class GroceryCategoryStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<CategoryMutationOutcome<GroceryCategorySetup>> EnsureForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member => member.UserId == userId && member.FullWorthSpaceId == fullWorthSpaceId, ct))
            return new(CategoryMutationResult.NotFound);
        if (!await SpaceCapabilities.HasCapabilityAsync(db, userId, fullWorthSpaceId, "transactions.categorize", ct))
            return new(CategoryMutationResult.Forbidden);

        var language = await db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId)
            .Select(space => space.DefaultCategoryLanguage)
            .SingleOrDefaultAsync(ct);
        var all = await db.Categories.Where(category => category.FullWorthSpaceId == fullWorthSpaceId).ToListAsync(ct);
        var byKey = all.ToDictionary(category => category.Key, StringComparer.OrdinalIgnoreCase);
        var treeKeys = FullWorthSeeder.GroceryTree.Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int created = 0, adopted = 0;
        foreach (var (key, englishName, parentKey, sortOrder) in FullWorthSeeder.GroceryTree)
        {
            if (byKey.ContainsKey(key) || !byKey.TryGetValue(parentKey, out var parent) || parent.IsArchived) continue;

            var name = DefaultCategoryNames.For(key, language ?? DefaultCategoryNames.German, englishName);
            var own = all.FirstOrDefault(category =>
                !category.IsArchived && !treeKeys.Contains(category.Key) &&
                (string.Equals(category.Name.Trim(), name, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(category.Name.Trim(), englishName, StringComparison.OrdinalIgnoreCase)));
            if (own is not null)
            {
                byKey.Remove(own.Key);
                own.Key = key;
                own.ParentId = parent.Id;
                byKey[key] = own;
                adopted++;
                continue;
            }

            var entity = new FinanceCategory { FullWorthSpaceId = fullWorthSpaceId, Key = key, Name = name, ParentId = parent.Id, SortOrder = sortOrder, IsSystem = true };
            db.Categories.Add(entity);
            all.Add(entity);
            byKey[key] = entity;
            created++;
        }

        if (created + adopted > 0)
        {
            audit.Record(fullWorthSpaceId, userId, "category.grocery-tree", "FinanceCategory", fullWorthSpaceId);
            await db.SaveChangesAsync(ct);
        }
        return new(CategoryMutationResult.Success, new GroceryCategorySetup(created, adopted, treeKeys.Count));
    }
}
