using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>Die erfassten Wohnkosten. Sie gehoeren dem Benutzer, nicht dem Space: jede Abfrage nennt die UserId.</summary>
public sealed class HousingCostStore(FullWorthDbContext db)
{
    public async Task<IReadOnlyList<HousingCostView>> ListAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        await db.Database.SqlQuery<HousingCostView>($"""
            SELECT "Id", "Kind", "PeriodFrom", "PeriodTo", "Amount", "Note", "PaperlessDocumentId", "CreatedAt"
            FROM "HousingCostEntries"
            WHERE "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId}
            ORDER BY "PeriodFrom", "Kind"
            """).ToListAsync(ct);

    public async Task<Guid> AddAsync(Guid userId, Guid fullWorthSpaceId, HousingCostWrite write, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "HousingCostEntries"
                ("Id", "FullWorthSpaceId", "UserId", "Kind", "PeriodFrom", "PeriodTo", "Amount", "Note", "PaperlessDocumentId", "CreatedAt", "UpdatedAt")
            VALUES ({id}, {fullWorthSpaceId}, {userId}, {write.Kind}, {write.PeriodFrom}, {write.PeriodTo}, {write.Amount},
                    {CleanNote(write.Note)}, {write.PaperlessDocumentId}, {now}, {now})
            """, ct);
        return id;
    }

    public async Task<bool> UpdateAsync(Guid userId, Guid fullWorthSpaceId, Guid id, HousingCostWrite write, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "HousingCostEntries"
            SET "Kind" = {write.Kind}, "PeriodFrom" = {write.PeriodFrom}, "PeriodTo" = {write.PeriodTo}, "Amount" = {write.Amount},
                "Note" = {CleanNote(write.Note)}, "PaperlessDocumentId" = {write.PaperlessDocumentId}, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {id} AND "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId}
            """, ct) > 0;

    public async Task<bool> DeleteAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "HousingCostEntries" WHERE "Id" = {id} AND "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId}
            """, ct) > 0;

    /// <summary>Alles, was ein Benutzer hier hat, in jedem Space - fuer die Kontoloeschung.</summary>
    public async Task DeleteAllForUserAsync(Guid userId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "HousingCostEntries" WHERE "UserId" = {userId}""", ct);

    /// <summary>Wirft <see cref="ArgumentException"/> bei Werten, die keine Wohnkosten sein koennen.</summary>
    public static void Validate(HousingCostWrite x)
    {
        if (!HousingCostKinds.IsKnown(x.Kind)) throw new ArgumentException("Unbekannte Kostenart.");
        if (x.PeriodFrom.Year is < 1990 or > 2100 || x.PeriodTo.Year is < 1990 or > 2100) throw new ArgumentException("Ungültiger Zeitraum.");
        if (x.PeriodTo < x.PeriodFrom) throw new ArgumentException("Das Ende des Zeitraums liegt vor dem Anfang.");
        if (x.PeriodTo.DayNumber - x.PeriodFrom.DayNumber > 366) throw new ArgumentException("Ein Zeitraum umfasst höchstens ein Jahr.");
        if (x.Amount is <= 0m or > 1_000_000m) throw new ArgumentException("Ungültiger Betrag.");
    }

    private static string? CleanNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)];
}
