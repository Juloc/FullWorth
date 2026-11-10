using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Die bestaetigten Vertragsstaende. Wie die Abrechnungen gehoeren sie dem Benutzer, nicht dem Space: jede
/// Abfrage nennt die UserId.
/// </summary>
public sealed class EmploymentContractStore(FullWorthDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ContractStateView>> ListAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<StateRow>($"""
            SELECT c."Id", c."Payload"::text AS "Payload", c."CreatedAt", i."Id" AS "ImportId", i."PaperlessUrl", i."PaperlessDocumentId",
                   i."FileName", (i."Extraction"->>'confidencePercent')::numeric AS "Confidence"
            FROM "EmploymentContractStates" c
            LEFT JOIN "CompensationDocumentImports" i ON i."ConfirmedId" = c."Id" AND i."UserId" = c."UserId"
            WHERE c."UserId" = {userId} AND c."FullWorthSpaceId" = {fullWorthSpaceId}
            ORDER BY c."ValidFrom", c."CreatedAt"
            """).ToListAsync(ct);
        return rows.Select(x => new ContractStateView(
            x.Id, fullWorthSpaceId, JsonSerializer.Deserialize<ContractStateWrite>(x.Payload, Json)!, x.CreatedAt,
            x.ImportId is null ? null : new DocumentRef(x.ImportId.Value, x.PaperlessUrl!, x.PaperlessDocumentId ?? 0, x.FileName, x.Confidence))).ToList();
    }

    /// <summary>
    /// Legt einen Stand an. Mit <paramref name="importId"/> bestaetigt er einen Fund aus Paperless: Stand und
    /// Verknuepfung entstehen in einer Anweisung, und gibt es keinen offenen Fund dieses Benutzers mit dieser
    /// Nummer, entsteht nichts (null).
    /// </summary>
    public async Task<Guid?> AddAsync(Guid userId, Guid fullWorthSpaceId, ContractStateWrite write, Guid? importId, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var json = JsonSerializer.Serialize(Clean(write), Json);
        var now = DateTimeOffset.UtcNow;
        if (importId is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "EmploymentContractStates" ("Id", "FullWorthSpaceId", "UserId", "ValidFrom", "Payload", "CreatedAt", "UpdatedAt")
                VALUES ({id}, {fullWorthSpaceId}, {userId}, {write.ValidFrom}, {json}::jsonb, {now}, {now})
                """, ct);
            return id;
        }

        var added = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH found AS (
                SELECT "Id" FROM "CompensationDocumentImports"
                WHERE "Id" = {importId} AND "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId}
                  AND "Kind" = {DocumentKinds.Contract} AND "Status" = {DocumentImportStatuses.NeedsReview}
                FOR UPDATE),
            linked AS (
                UPDATE "CompensationDocumentImports" SET "Status" = {DocumentImportStatuses.Confirmed}, "ConfirmedId" = {id}, "UpdatedAt" = {now}
                WHERE "Id" IN (SELECT "Id" FROM found)
                RETURNING 1)
            INSERT INTO "EmploymentContractStates" ("Id", "FullWorthSpaceId", "UserId", "ValidFrom", "Payload", "CreatedAt", "UpdatedAt")
            SELECT {id}, {fullWorthSpaceId}, {userId}, {write.ValidFrom}, {json}::jsonb, {now}, {now} FROM found
            """, ct);
        return added == 0 ? null : id;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "EmploymentContractStates" WHERE "Id" = {id} AND "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId}
            """, ct) > 0;

    /// <summary>Alle Staende eines Benutzers, in jedem Space - fuer die Kontoloeschung.</summary>
    public async Task DeleteAllForUserAsync(Guid userId, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "EmploymentContractStates" WHERE "UserId" = {userId}""", ct);

    /// <summary>Wirft <see cref="ArgumentException"/> bei Werten, die kein Vertrag haben kann.</summary>
    public static void Validate(ContractStateWrite x)
    {
        if (x.ValidFrom.Year is < 1980 or > 2100) throw new ArgumentException("Ungültiges Datum für „gültig ab“.");
        if (x.Kind is not (ContractKinds.Contract or ContractKinds.Addendum)) throw new ArgumentException("Art muss Vertrag oder Nachtrag sein.");
        if (x.GrossMonthly is <= 0m or > 1_000_000m) throw new ArgumentException("Ungültiges Bruttogehalt.");
        if (x.WeeklyHours is < 0m or > 80m) throw new ArgumentException("Ungültige Wochenstunden.");
        if (x.VacationDays is < 0m or > 100m) throw new ArgumentException("Ungültige Urlaubstage.");
        if (x.BavEmployerShare is < 0m or > 100_000m) throw new ArgumentException("Ungültiger bAV-Arbeitgeberanteil.");
        if ((x.Extras?.Count ?? 0) > 20) throw new ArgumentException("Höchstens 20 weitere Felder.");
    }

    private static ContractStateWrite Clean(ContractStateWrite x) => x with
    {
        Employer = Trim(x.Employer, 200), NoticePeriod = Trim(x.NoticePeriod, 200), Position = Trim(x.Position, 200), Note = Trim(x.Note, 500),
        Extras = x.Extras?.Where(e => !string.IsNullOrWhiteSpace(e.Label) && !string.IsNullOrWhiteSpace(e.Value))
            .Select(e => new ContractField(e.Label.Trim()[..Math.Min(e.Label.Trim().Length, 80)], e.Value.Trim()[..Math.Min(e.Value.Trim().Length, 200)])).ToList()
    };

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    private sealed class StateRow
    {
        public Guid Id { get; set; }
        public string Payload { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public Guid? ImportId { get; set; }
        public string? PaperlessUrl { get; set; }
        public int? PaperlessDocumentId { get; set; }
        public string? FileName { get; set; }
        public decimal? Confidence { get; set; }
    }
}
