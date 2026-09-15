using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;

namespace FullWorth.Backend.Modules.FullWorthSpaces;

public sealed class FullWorthSpaceService(FullWorthDbContext db, FullWorthSpaceStore store, FullWorthSeeder seeder, AuditService audit)
{
    /// <param name="categoryLanguage">
    /// In welcher Sprache die Standardkategorien angelegt werden: <c>de</c> oder <c>en</c>. Null heisst
    /// Englisch - das war bis #117 die einzige Moeglichkeit.
    /// </param>
    public async Task<FullWorthSpace> CreateAsync(
        Guid ownerUserId, string name, string? baseCurrency, CancellationToken ct, string? categoryLanguage = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var space = await store.CreateAsync(ownerUserId, name, baseCurrency, ct);
        await seeder.SeedDefaultCategoriesForSpaceAsync(db, space.Id, ct, categoryLanguage);
        audit.Record(space.Id, ownerUserId, "space.created", "FullWorthSpace", space.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return space;
    }
}
