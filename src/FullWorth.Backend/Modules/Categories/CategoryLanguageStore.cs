using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

public sealed record CategoryLanguageState(string Language, bool CanChange, int SystemCategories, int RenamedByUser);

public enum CategoryLanguageResult { Applied, NoChange, Locked, NotFound }

/// <summary>
/// Die Sprache der Standardkategorien nachtraeglich umstellen - solange niemand sie angefasst hat.
///
/// Der Space wird beim Anlegen in einer Sprache gesaet; der Einrichtungsassistent laeuft erst danach.
/// Damit die Frage im Assistenten nicht folgenlos bleibt, benennt diese Stelle die Standardkategorien
/// um - nach SCHLUESSEL, nicht nach Namen, denn der Schluessel ist das Stabile und jede Regel zielt
/// darauf. Es entsteht keine einzige neue Zeile.
///
/// Die Grenze ist eng gezogen und das ist Absicht: sobald ein Benutzer eine Standardkategorie selbst
/// umbenannt hat, wird gar nichts mehr umgestellt. Eine Umbenennung ist eine Entscheidung, und eine
/// Spracheinstellung darf sie nicht ueberschreiben. Selbst gemachte Kategorien (IsSystem = false)
/// bleiben in jedem Fall unberuehrt - auch die, die ein Import angelegt hat.
/// </summary>
public sealed class CategoryLanguageStore(FullWorthDbContext db)
{
    public async Task<CategoryLanguageState?> ReadAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var space = await db.FullWorthSpaces.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == fullWorthSpaceId, ct);
        if (space is null) return null;

        var language = DefaultCategoryNames.Normalize(space.DefaultCategoryLanguage);
        var system = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId && category.IsSystem)
            .Select(category => new { category.Key, category.Name })
            .ToListAsync(ct);

        var renamed = system.Count(row => !IsDefaultName(row.Key, row.Name));
        return new CategoryLanguageState(language, renamed == 0 && system.Count > 0, system.Count, renamed);
    }

    public async Task<CategoryLanguageResult> ApplyAsync(Guid fullWorthSpaceId, string language, CancellationToken ct)
    {
        var target = DefaultCategoryNames.Normalize(language);
        var space = await db.FullWorthSpaces.SingleOrDefaultAsync(item => item.Id == fullWorthSpaceId, ct);
        if (space is null) return CategoryLanguageResult.NotFound;

        var system = await db.Categories
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId && category.IsSystem)
            .ToListAsync(ct);
        if (system.Count == 0) return CategoryLanguageResult.NotFound;
        if (system.Any(category => !IsDefaultName(category.Key, category.Name)))
            return CategoryLanguageResult.Locked;

        if (DefaultCategoryNames.Normalize(space.DefaultCategoryLanguage) == target)
            return CategoryLanguageResult.NoChange;

        var now = DateTimeOffset.UtcNow;
        foreach (var category in system)
        {
            var name = FullWorthSeeder.DefaultNameFor(category.Key, target);
            if (name is null || string.Equals(category.Name, name, StringComparison.Ordinal)) continue;
            category.Name = name;
        }

        space.DefaultCategoryLanguage = target;
        space.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return CategoryLanguageResult.Applied;
    }

    /// <summary>Traegt die Kategorie noch einen der beiden Standardnamen fuer ihren Schluessel?</summary>
    private static bool IsDefaultName(string key, string name) =>
        string.Equals(FullWorthSeeder.DefaultNameFor(key, DefaultCategoryNames.German), name, StringComparison.Ordinal)
        || string.Equals(FullWorthSeeder.DefaultNameFor(key, DefaultCategoryNames.English), name, StringComparison.Ordinal);
}
