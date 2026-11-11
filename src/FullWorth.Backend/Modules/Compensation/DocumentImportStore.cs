using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Documents;
using FullWorth.Backend.Modules.Paperless;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Die Funde aus Paperless - Lohnabrechnungen und Arbeitsvertraege - und ihre PDF. Alles hier gehoert dem Benutzer, der den Fund
/// angelegt hat: jede Abfrage nach einzelnen Zeilen nennt seine UserId, nicht nur den Space - ein
/// anderes Mitglied desselben Spaces findet weder die Zeile noch die Datei.
/// </summary>
public sealed class DocumentImportStore(FullWorthDbContext db, IOptions<CompensationStorageOptions> storage, FieldCipher cipher)
{
    // Eigene HKDF-Bezeichnung: ein geleakter Schluessel dieser Dateien oeffnet keine Altersvorsorge-Dokumente.
    private readonly EncryptedBlobStore blobs = new(
        storage.Value.RootPath, storage.Value.MaxDocumentBytes, cipher.DeriveSubKey("fullworth-compensation-blob"));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxAttempts = 3;

    /// <summary>Welche der Dokumente dieser Instanz der Benutzer schon kennt - an der Dokumentnummer oder an der Pruefsumme.</summary>
    public async Task<(HashSet<int> Ids, HashSet<string> Checksums)> GetKnownAsync(
        Guid userId, Guid fullWorthSpaceId, string source, IReadOnlyCollection<PaperlessDocumentSummary> documents, CancellationToken ct)
    {
        var ids = documents.Select(x => x.Id).ToArray();
        var checksums = documents.Where(x => x.Checksum is not null).Select(x => x.Checksum!).ToArray();
        var rows = await db.Database.SqlQuery<KnownRow>($"""
            SELECT "PaperlessDocumentId" AS "DocumentId", "Checksum"
            FROM "CompensationDocumentImports"
            WHERE "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId}
              AND (("PaperlessSource" = {source} AND "PaperlessDocumentId" = ANY({ids})) OR "Checksum" = ANY({checksums}))
            """).ToListAsync(ct);
        return (rows.Select(x => x.DocumentId).ToHashSet(), rows.Where(x => x.Checksum is not null).Select(x => x.Checksum!).ToHashSet());
    }

    public async Task<bool> AddPendingAsync(
        Guid userId, Guid fullWorthSpaceId, string kind, string source, PaperlessDocumentSummary document, string paperlessUrl, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var title = string.IsNullOrWhiteSpace(document.Title) ? $"Paperless {document.Id}" : document.Title.Trim();
        var added = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CompensationDocumentImports"
                ("Id", "FullWorthSpaceId", "UserId", "Kind", "PaperlessSource", "PaperlessDocumentId", "PaperlessUrl",
                 "Checksum", "Title", "Status", "CreatedAt", "UpdatedAt")
            VALUES
                ({Guid.NewGuid()}, {fullWorthSpaceId}, {userId}, {kind}, {source}, {document.Id}, {Cap(paperlessUrl, 1000)},
                 {document.Checksum}, {Cap(title, 300)}, {DocumentImportStatuses.Pending}, {now}, {now})
            ON CONFLICT ("UserId", "FullWorthSpaceId", "PaperlessSource", "PaperlessDocumentId") DO NOTHING
            """, ct);
        return added > 0;
    }

    public async Task<IReadOnlyList<PendingDocumentImport>> ListPendingAsync(int limit, CancellationToken ct) =>
        await db.Database.SqlQuery<PendingDocumentImport>($"""
            SELECT "Id", "FullWorthSpaceId", "UserId", "Kind", "PaperlessSource", "PaperlessDocumentId", "Title"
            FROM "CompensationDocumentImports"
            WHERE "Status" = {DocumentImportStatuses.Pending}
            ORDER BY "CreatedAt"
            LIMIT {limit}
            """).ToListAsync(ct);

    /// <summary>Legt das PDF verschluesselt ab und haengt die ausgelesenen Werte an den Fund - er wartet dann auf die Bestaetigung.</summary>
    public async Task StoreReadAsync(
        Guid id, byte[] content, string fileName, string contentType, object extraction, CancellationToken ct)
    {
        // Eine neue Nummer je Ablage statt der des Fundes: lesen zwei Durchgaenge denselben Fund, schreibt
        // jeder seine eigene Datei, und der, dem die Zeile nicht mehr gehoert, loescht nur seine.
        var blob = await blobs.WriteAsync(Guid.NewGuid(), content, ct);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "CompensationDocumentImports"
            SET "Status" = {DocumentImportStatuses.NeedsReview}, "Error" = NULL,
                "Extraction" = {JsonSerializer.Serialize(extraction, Json)}::jsonb,
                "FileName" = {Cap(Path.GetFileName(fileName), 255)}, "ContentType" = {Cap(contentType, 100)},
                "StoragePath" = {blob.StoragePath}, "EncryptionScheme" = {blob.EncryptionScheme},
                "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {id} AND "Status" = {DocumentImportStatuses.Pending}
            """, ct);
        // Verworfen, waehrend es gelesen wurde: die Datei hat keine Zeile mehr, die auf sie zeigt.
        if (updated == 0) await blobs.DeleteAsync(blob.StoragePath, ct);
    }

    /// <summary>Ein Fehlschlag zaehlt; beim dritten ist der Fund "failed" und wird nicht mehr versucht.</summary>
    public async Task RecordFailureAsync(Guid id, string error, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "CompensationDocumentImports"
            SET "Attempts" = "Attempts" + 1, "Error" = {Cap(error, 1000)}, "UpdatedAt" = {DateTimeOffset.UtcNow},
                "Status" = CASE WHEN "Attempts" + 1 >= {MaxAttempts} THEN {DocumentImportStatuses.Failed} ELSE {DocumentImportStatuses.Pending} END
            WHERE "Id" = {id} AND "Status" = {DocumentImportStatuses.Pending}
            """, ct);

    public async Task<IReadOnlyList<DocumentImportView>> ListOpenAsync(Guid userId, Guid fullWorthSpaceId, string kind, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<OpenRow>($"""
            SELECT "Id", "Title", "Status", "Error", "PaperlessUrl", "Extraction"::text AS "Extraction", "CreatedAt"
            FROM "CompensationDocumentImports"
            WHERE "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "Kind" = {kind}
              AND "Status" IN ({DocumentImportStatuses.Pending}, {DocumentImportStatuses.NeedsReview}, {DocumentImportStatuses.Failed})
            ORDER BY "CreatedAt" DESC, "Title"
            """).ToListAsync(ct);
        return rows.Select(x => new DocumentImportView(
            x.Id, x.Title, x.Status, x.Error, x.PaperlessUrl,
            x.Extraction is null ? null : JsonSerializer.Deserialize<JsonElement>(x.Extraction, Json),
            x.CreatedAt)).ToList();
    }

    /// <summary>Das abgelegte Original; null, wenn es nicht dem Benutzer gehoert, keins hat oder die Datei fehlt.</summary>
    public async Task<DocumentContent?> GetDocumentAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<DocumentRow>($"""
            SELECT "StoragePath", "EncryptionScheme", "ContentType", "FileName"
            FROM "CompensationDocumentImports"
            WHERE "Id" = {id} AND "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "StoragePath" IS NOT NULL
            """).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var content = await blobs.ReadAsync(rows[0].StoragePath, rows[0].EncryptionScheme, ct);
        return content is null ? null : new DocumentContent(content, rows[0].ContentType ?? "application/octet-stream", rows[0].FileName ?? $"lohnabrechnung-{id:N}.pdf");
    }

    /// <summary>
    /// Verwirft einen offenen Fund: Werte und Datei sind weg, die Zeile bleibt als Erinnerung, dass das
    /// Dokument schon gesehen wurde - sonst holte der naechste Lauf es wieder. False, wenn es keinen
    /// offenen Fund dieses Benutzers gibt.
    /// </summary>
    public Task<bool> DismissAsync(Guid userId, Guid id, CancellationToken ct) =>
        ReleaseAsync(userId, id, null, [DocumentImportStatuses.Pending, DocumentImportStatuses.NeedsReview, DocumentImportStatuses.Failed], ct);

    /// <summary>
    /// Schickt alle offenen, ausgelesenen Funde einer Art noch einmal durchs Lesen - nach einer Verbesserung des
    /// Lesers, oder weil ein Fund nicht ausgelesen werden konnte. Die alten Werte und die abgelegte Datei sind weg;
    /// der Leser holt das Original frisch aus Paperless. Bestaetigtes bleibt unberuehrt.
    /// </summary>
    public async Task<int> RequeueOpenAsync(Guid userId, Guid fullWorthSpaceId, string kind, CancellationToken ct)
    {
        string[] statuses = [DocumentImportStatuses.NeedsReview, DocumentImportStatuses.Failed];
        var paths = await db.Database.SqlQuery<PathRow>($"""
            SELECT "StoragePath" AS "Value" FROM "CompensationDocumentImports"
            WHERE "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "Kind" = {kind}
              AND "Status" = ANY({statuses}) AND "StoragePath" IS NOT NULL
            """).ToListAsync(ct);
        var requeued = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "CompensationDocumentImports"
            SET "Status" = {DocumentImportStatuses.Pending}, "Attempts" = 0, "Error" = NULL, "Extraction" = NULL,
                "StoragePath" = NULL, "EncryptionScheme" = NULL, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "UserId" = {userId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "Kind" = {kind}
              AND "Status" = ANY({statuses})
            """, ct);
        foreach (var path in paths) await blobs.DeleteAsync(path.Value, ct);
        return requeued;
    }

    /// <summary>Die bestaetigte Abrechnung wurde geloescht: auch ihr Original verschwindet, die Erinnerung bleibt.</summary>
    public Task<bool> ReleaseConfirmedAsync(Guid userId, Guid confirmedId, CancellationToken ct) =>
        ReleaseAsync(userId, null, confirmedId, [DocumentImportStatuses.Confirmed], ct);

    /// <summary>Alles, was ein Benutzer hier hat, samt Dateien - fuer die Kontoloeschung. Gilt in jedem Space.</summary>
    public async Task DeleteAllForUserAsync(Guid userId, CancellationToken ct)
    {
        var paths = await db.Database.SqlQuery<PathRow>($"""
            SELECT "StoragePath" AS "Value" FROM "CompensationDocumentImports" WHERE "UserId" = {userId} AND "StoragePath" IS NOT NULL
            """).ToListAsync(ct);
        foreach (var path in paths) await blobs.DeleteAsync(path.Value, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "CompensationDocumentImports" WHERE "UserId" = {userId}""", ct);
    }

    // Genau einer von id und confirmedId ist gesetzt; der andere Zweig der Bedingung ist dann ohne Wirkung.
    private async Task<bool> ReleaseAsync(Guid userId, Guid? id, Guid? confirmedId, string[] fromStatuses, CancellationToken ct)
    {
        var paths = await db.Database.SqlQuery<PathRow>($"""
            SELECT "StoragePath" AS "Value" FROM "CompensationDocumentImports"
            WHERE "UserId" = {userId} AND ("Id" = {id} OR {id}::uuid IS NULL) AND ("ConfirmedId" = {confirmedId} OR {confirmedId}::uuid IS NULL)
              AND "Status" = ANY({fromStatuses}) AND "StoragePath" IS NOT NULL
            """).ToListAsync(ct);
        var released = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "CompensationDocumentImports"
            SET "Status" = {DocumentImportStatuses.Dismissed}, "Extraction" = NULL, "Error" = NULL, "ConfirmedId" = NULL,
                "StoragePath" = NULL, "EncryptionScheme" = NULL, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "UserId" = {userId} AND ("Id" = {id} OR {id}::uuid IS NULL) AND ("ConfirmedId" = {confirmedId} OR {confirmedId}::uuid IS NULL)
              AND "Status" = ANY({fromStatuses})
            """, ct);
        foreach (var path in paths) await blobs.DeleteAsync(path.Value, ct);
        return released > 0;
    }

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed class KnownRow { public int DocumentId { get; set; } public string? Checksum { get; set; } }
    private sealed class OpenRow
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? Error { get; set; }
        public string PaperlessUrl { get; set; } = string.Empty;
        public string? Extraction { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
    private sealed class DocumentRow
    {
        public string StoragePath { get; set; } = string.Empty;
        public string? EncryptionScheme { get; set; }
        public string? ContentType { get; set; }
        public string? FileName { get; set; }
    }
    private sealed class PathRow { public string Value { get; set; } = string.Empty; }
}
