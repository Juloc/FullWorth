using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Die fertigen Zusammenfassungen lesen. Geschrieben werden sie vom IntelligenceDigestService, der
/// sie baut - das ist ein anderer Vorgang und darum ein anderer Ort.
/// </summary>
public sealed class IntelligenceDigestStore(IntelligenceDbContext db)
{
    /// <summary>Hoechstens hundert, neueste zuerst.</summary>
    public const int MaxLimit = 100;

    public Task<List<IntelligenceDigest>> ListAsync(Guid fullWorthSpaceId, int limit, CancellationToken ct) =>
        db.IntelligenceDigests.AsNoTracking()
            .Where(digest => digest.FullWorthSpaceId == fullWorthSpaceId)
            .OrderByDescending(digest => digest.PeriodStart)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .ToListAsync(ct);

    // FindAsync stand hier bis #177 und bediente den Detailabruf einer einzelnen
    // Zusammenfassung. Die Route ist weg - sie lieferte dieselbe Ansicht wie die Liste.
}
