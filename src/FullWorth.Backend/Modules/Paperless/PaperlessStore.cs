using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Paperless;

/// <summary>
/// Die Paperless-Verbindung eines Spaces und die Presets, die daran haengen. Beides gehoert keinem
/// Fachmodul: Belege und Lohnabrechnungen lesen dieselbe Verbindung.
/// </summary>
public sealed class PaperlessStore(FullWorthDbContext db)
{
    public async Task TouchPaperlessSyncAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PaperlessReceiptConnections"
            SET "LastSyncAt" = {DateTimeOffset.UtcNow}, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "FullWorthSpaceId" = {fullWorthSpaceId}
            """, ct);
    }

    public async Task<PaperlessStoredConnection?> GetPaperlessConnectionAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<PaperlessConnectionProjection>($"""
            SELECT "FullWorthSpaceId", "UserId", "BaseUrl", "ApiTokenProtected", "DefaultQuery", "IsEnabled",
                   "LastSyncAt", "CreatedAt", "UpdatedAt"
            FROM "PaperlessReceiptConnections"
            WHERE "FullWorthSpaceId" = {fullWorthSpaceId}
            LIMIT 1
            """).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var x = rows[0];
        return new PaperlessStoredConnection(x.FullWorthSpaceId, x.UserId, x.BaseUrl, x.ApiTokenProtected, x.DefaultQuery, x.IsEnabled, x.LastSyncAt, x.CreatedAt, x.UpdatedAt);
    }

    public async Task UpsertPaperlessConnectionAsync(
        Guid fullWorthSpaceId,
        Guid userId,
        string baseUrl,
        string protectedToken,
        string? defaultQuery,
        bool enabled,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PaperlessReceiptConnections"
                ("FullWorthSpaceId", "UserId", "BaseUrl", "ApiTokenProtected", "DefaultQuery", "IsEnabled", "CreatedAt", "UpdatedAt")
            VALUES ({fullWorthSpaceId}, {userId}, {Cap(baseUrl, 1000)}, {protectedToken}, {CapNullable(defaultQuery, 1000)}, {enabled}, {now}, {now})
            ON CONFLICT ("FullWorthSpaceId") DO UPDATE SET
                "UserId" = EXCLUDED."UserId",
                "BaseUrl" = EXCLUDED."BaseUrl",
                "ApiTokenProtected" = EXCLUDED."ApiTokenProtected",
                "DefaultQuery" = EXCLUDED."DefaultQuery",
                "IsEnabled" = EXCLUDED."IsEnabled",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);
    }

    public async Task DeletePaperlessConnectionAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "PaperlessReceiptConnections" WHERE "FullWorthSpaceId" = {fullWorthSpaceId}
            """, ct);
    }

    public async Task DisablePaperlessAutoImportAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PaperlessImportPresets"
            SET "AutoImport" = false, "LastError" = NULL, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "FullWorthSpaceId" = {fullWorthSpaceId} AND "AutoImport" = true
            """, ct);
    }

    public async Task<IReadOnlyList<PaperlessImportPresetView>> ListPaperlessPresetsAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<PaperlessPresetProjection>($"""
            SELECT "Id", "FullWorthSpaceId", "UserId", "Name", "Query", "EditorJson", "AutoImport",
                   "AnalyzeAutomatically", "Currency", "LastSeenDocumentId", "LastCheckedAt",
                   "LastImportedAt", "LastError", "CreatedAt", "UpdatedAt"
            FROM "PaperlessImportPresets"
            WHERE "FullWorthSpaceId" = {fullWorthSpaceId} AND "UserId" = {userId}
            ORDER BY lower("Name"), "CreatedAt"
            """).ToListAsync(ct);
        return rows.Select(ToPresetView).ToList();
    }

    public async Task<PaperlessImportPresetView?> GetPaperlessPresetAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid presetId,
        CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<PaperlessPresetProjection>($"""
            SELECT "Id", "FullWorthSpaceId", "UserId", "Name", "Query", "EditorJson", "AutoImport",
                   "AnalyzeAutomatically", "Currency", "LastSeenDocumentId", "LastCheckedAt",
                   "LastImportedAt", "LastError", "CreatedAt", "UpdatedAt"
            FROM "PaperlessImportPresets"
            WHERE "Id" = {presetId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "UserId" = {userId}
            LIMIT 1
            """).ToListAsync(ct);
        return rows.Count == 0 ? null : ToPresetView(rows[0]);
    }

    public async Task<PaperlessImportPresetView> SavePaperlessPresetAsync(
        Guid id,
        Guid userId,
        Guid fullWorthSpaceId,
        string name,
        string? query,
        string? editorJson,
        bool autoImport,
        bool analyzeAutomatically,
        string currency,
        int? lastSeenDocumentId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PaperlessImportPresets"
                ("Id", "FullWorthSpaceId", "UserId", "Name", "Query", "EditorJson", "AutoImport",
                 "AnalyzeAutomatically", "Currency", "LastSeenDocumentId", "CreatedAt", "UpdatedAt")
            VALUES
                ({id}, {fullWorthSpaceId}, {userId}, {Cap(name.Trim(), 100)}, {CapNullable(query, 4000)},
                 {editorJson}, {autoImport}, {analyzeAutomatically}, {currency}, {lastSeenDocumentId}, {now}, {now})
            ON CONFLICT ("Id") DO UPDATE SET
                "Name" = EXCLUDED."Name",
                "Query" = EXCLUDED."Query",
                "EditorJson" = EXCLUDED."EditorJson",
                "AutoImport" = EXCLUDED."AutoImport",
                "AnalyzeAutomatically" = EXCLUDED."AnalyzeAutomatically",
                "Currency" = EXCLUDED."Currency",
                "LastSeenDocumentId" = EXCLUDED."LastSeenDocumentId",
                "LastError" = NULL,
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, ct);

        return await GetPaperlessPresetAsync(userId, fullWorthSpaceId, id, ct)
            ?? throw new InvalidOperationException("Paperless import preset could not be saved.");
    }

    public async Task DeletePaperlessPresetAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid presetId,
        CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "PaperlessImportPresets"
            WHERE "Id" = {presetId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "UserId" = {userId}
            """, ct);
    }

    public async Task<IReadOnlyList<PaperlessAutoImportTarget>> ListAutoImportPresetsAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<PaperlessPresetProjection>($"""
            SELECT "Id", "FullWorthSpaceId", "UserId", "Name", "Query", "EditorJson", "AutoImport",
                   "AnalyzeAutomatically", "Currency", "LastSeenDocumentId", "LastCheckedAt",
                   "LastImportedAt", "LastError", "CreatedAt", "UpdatedAt"
            FROM "PaperlessImportPresets"
            WHERE "AutoImport" = true
            ORDER BY "FullWorthSpaceId", "CreatedAt"
            """).ToListAsync(ct);
        return rows.Select(x => new PaperlessAutoImportTarget(
            x.Id, x.FullWorthSpaceId, x.UserId, x.Name, x.Query, x.AnalyzeAutomatically, x.Currency, x.LastSeenDocumentId)).ToList();
    }

    public async Task UpdatePaperlessPresetCheckAsync(
        Guid presetId,
        int? lastSeenDocumentId,
        bool imported,
        string? error,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "PaperlessImportPresets"
            SET "LastCheckedAt" = {now},
                "LastImportedAt" = CASE WHEN {imported} THEN {now} ELSE "LastImportedAt" END,
                "LastSeenDocumentId" = COALESCE({lastSeenDocumentId}, "LastSeenDocumentId"),
                "LastError" = {CapNullable(error, 1000)},
                "UpdatedAt" = {now}
            WHERE "Id" = {presetId}
            """, ct);
    }

    private static PaperlessImportPresetView ToPresetView(PaperlessPresetProjection x) =>
        new(x.Id, x.FullWorthSpaceId, x.UserId, x.Name, x.Query, x.EditorJson, x.AutoImport,
            x.AnalyzeAutomatically, x.Currency, x.LastSeenDocumentId, x.LastCheckedAt, x.LastImportedAt,
            x.LastError, x.CreatedAt, x.UpdatedAt);

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];
    private static string? CapNullable(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : Cap(value.Trim(), max);

    private sealed class PaperlessPresetProjection
    {
        public Guid Id { get; set; }
        public Guid FullWorthSpaceId { get; set; }
        public Guid UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Query { get; set; }
        public string? EditorJson { get; set; }
        public bool AutoImport { get; set; }
        public bool AnalyzeAutomatically { get; set; }
        public string Currency { get; set; } = "EUR";
        public int? LastSeenDocumentId { get; set; }
        public DateTimeOffset? LastCheckedAt { get; set; }
        public DateTimeOffset? LastImportedAt { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class PaperlessConnectionProjection
    {
        public Guid FullWorthSpaceId { get; set; }
        public Guid UserId { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiTokenProtected { get; set; } = string.Empty;
        public string? DefaultQuery { get; set; }
        public bool IsEnabled { get; set; }
        public DateTimeOffset? LastSyncAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
