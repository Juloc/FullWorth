using FullWorth.Backend.Modules.Reconciliation;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;

namespace FullWorth.Backend.Modules.Analytics;

/// <summary>Eine gespeicherte Auswertung, so wie sie in <c>SavedAnalyses</c> steht.</summary>
public sealed record SavedAnalysisRow(
    Guid Id, string Name, int SchemaVersion, string ConfigJson, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Die Auswertungen, die ein Benutzer sich gemerkt hat.
///
/// Jede Abfrage filtert auf <c>OwnerUserId</c>, nicht nur auf den Space: eine gespeicherte Auswertung
/// gehoert der Person, die sie angelegt hat, und nicht dem Haushalt. Darum genuegt beim Loeschen und
/// Aendern auch kein Mitgliedschaftsnachweis - die WHERE-Klausel ist die Pruefung.
/// </summary>
public sealed class SavedAnalysisStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<IReadOnlyList<SavedAnalysisRow>> ListAsync(
        Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"Id\",\"Name\",\"SchemaVersion\",\"ConfigJson\",\"CreatedAt\",\"UpdatedAt\" FROM \"SavedAnalyses\" WHERE \"FullWorthSpaceId\"=@space AND \"OwnerUserId\"=@uid ORDER BY \"Name\"",
            ("@space", fullWorthSpaceId), ("@uid", userId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<SavedAnalysisRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new SavedAnalysisRow(
                RawSql.Guid(reader, "Id"),
                RawSql.String(reader, "Name"),
                RawSql.Int(reader, "SchemaVersion"),
                RawSql.String(reader, "ConfigJson"),
                RawSql.Timestamp(reader, "CreatedAt"),
                RawSql.Timestamp(reader, "UpdatedAt")));
        return rows;
    }

    public async Task<Guid> CreateAsync(
        Guid userId, Guid fullWorthSpaceId, SavedAnalysisWrite request, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "INSERT INTO \"SavedAnalyses\" (\"Id\",\"FullWorthSpaceId\",\"OwnerUserId\",\"Name\",\"SchemaVersion\",\"ConfigJson\",\"CreatedAt\",\"UpdatedAt\") VALUES (@id,@space,@uid,@name,@version,CAST(@json AS jsonb),@now,@now)",
            ("@id", id), ("@space", fullWorthSpaceId), ("@uid", userId), ("@name", request.Name.Trim()),
            ("@version", request.SchemaVersion), ("@json", ConfigJson(request)), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);

        audit.Record(fullWorthSpaceId, userId, "analysis.saved", "SavedAnalysis", id);
        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task<bool> UpdateAsync(
        Guid userId, Guid fullWorthSpaceId, Guid id, SavedAnalysisWrite request, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "UPDATE \"SavedAnalyses\" SET \"Name\"=@name,\"SchemaVersion\"=@version,\"ConfigJson\"=CAST(@json AS jsonb),\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"OwnerUserId\"=@uid",
            ("@name", request.Name.Trim()), ("@version", request.SchemaVersion), ("@json", ConfigJson(request)),
            ("@now", DateTimeOffset.UtcNow), ("@id", id), ("@space", fullWorthSpaceId), ("@uid", userId));
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return false;

        audit.Record(fullWorthSpaceId, userId, "analysis.updated", "SavedAnalysis", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "DELETE FROM \"SavedAnalyses\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"OwnerUserId\"=@uid",
            ("@id", id), ("@space", fullWorthSpaceId), ("@uid", userId));
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return false;

        audit.Record(fullWorthSpaceId, userId, "analysis.deleted", "SavedAnalysis", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string ConfigJson(SavedAnalysisWrite request) =>
        JsonSerializer.Serialize(new { query = request.Query, chartType = request.ChartType });
}
