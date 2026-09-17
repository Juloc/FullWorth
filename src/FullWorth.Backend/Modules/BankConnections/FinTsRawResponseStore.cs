using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>Was die Bank geschickt hat - Kopfdaten, ohne den Inhalt.</summary>
public sealed record FinTsRawResponseItem(
    Guid Id, string Kind, string? Label, DateTimeOffset CapturedAt, int PayloadLength);

/// <summary>Dasselbe, mit dem Inhalt.</summary>
public sealed record FinTsRawResponseDetail(
    Guid Id, string Kind, string? Label, DateTimeOffset CapturedAt, int PayloadLength, string Payload);

/// <summary>Was die Banking-Seite ablegt.</summary>
public sealed record FinTsRawResponseWrite(string Kind, string? Label, string Payload);

/// <summary>
/// Die letzten Antworten der Bank, verschluesselt, damit niemand mehr raten muss.
///
/// Vier FinTS-Fehler hintereinander kosteten je einen vollen Umlauf aus Vermutung, Release, Abruf
/// und Logzeile - obwohl die Antwort jedes Mal vorlag. Der Parser liest acht Feldkennungen und
/// verwirft den Rest; danach ist die Frage nicht mehr zu stellen, ohne die Bank erneut zu fragen.
///
/// Zur Regel "keine FinTS-Nachricht im Klartext": die gilt der ANFRAGE, denn deren HNSHA traegt die
/// PIN, und sie gilt dem LOG. Die Antwort enthaelt Bestaende, und verschluesselt in der Datenbank
/// ist etwas anderes als offen im Containerlog. Herausgegeben wird sie nur an ein Mitglied des
/// Space, dem die Verbindung gehoert - dieselbe Pruefung wie beim Sync-Verlauf.
///
/// Aufgehoben wird eine kurze Kette je Verbindung und Art. Das hier ist ein Werkzeug zum Nachsehen,
/// kein Archiv - und was nicht da ist, kann auch nicht danebengehen.
/// </summary>
public sealed class FinTsRawResponseStore(FullWorthDbContext db, FieldCipher? fieldCipher = null)
{
    /// <summary>Wie viele Antworten je Verbindung und Art stehen bleiben.</summary>
    public const int Keep = 5;

    private readonly FieldCipher cipher = fieldCipher ?? FieldCipher.Null;

    public async Task<bool> RecordAsync(Guid connectionId, FinTsRawResponseWrite request, CancellationToken ct)
    {
        var kind = request.Kind.Trim();
        if (kind.Length == 0 || string.IsNullOrEmpty(request.Payload)) return false;
        if (!await db.BankConnections.AsNoTracking().AnyAsync(x => x.Id == connectionId, ct)) return false;

        var sql = await RawSql.OpenAsync(db, ct);
        await using (var insert = RawSql.Command(sql, """
INSERT INTO "FinTsRawResponses" ("Id","BankConnectionId","Kind","Label","CapturedAt","PayloadLength","Payload")
VALUES (@id,@connection,@kind,@label,@now,@length,@payload)
""",
            ("@id", Guid.NewGuid()), ("@connection", connectionId), ("@kind", kind),
            ("@label", string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim()),
            ("@now", DateTimeOffset.UtcNow), ("@length", request.Payload.Length),
            ("@payload", cipher.Protect(request.Payload))))
            await insert.ExecuteNonQueryAsync(ct);

        // Die Kette bleibt kurz. Geloescht wird nach dem Einfuegen, damit die neue Antwort auf jeden
        // Fall steht - lieber eine zu viel als die eine, auf die es ankam, zu wenig.
        await using var prune = RawSql.Command(sql, """
DELETE FROM "FinTsRawResponses" WHERE "Id" IN (
  SELECT "Id" FROM "FinTsRawResponses"
  WHERE "BankConnectionId"=@connection AND "Kind"=@kind
  ORDER BY "CapturedAt" DESC, "Id" DESC OFFSET @keep)
""", ("@connection", connectionId), ("@kind", kind), ("@keep", Keep));
        await prune.ExecuteNonQueryAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<FinTsRawResponseItem>?> ListForUserAsync(
        Guid userId, Guid fullWorthSpaceId, Guid connectionId, CancellationToken ct)
    {
        if (!await OwnsAsync(userId, fullWorthSpaceId, connectionId, ct)) return null;

        var sql = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(sql, """
SELECT "Id","Kind","Label","CapturedAt","PayloadLength" FROM "FinTsRawResponses"
WHERE "BankConnectionId"=@connection ORDER BY "CapturedAt" DESC, "Id" DESC
""", ("@connection", connectionId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<FinTsRawResponseItem>();
        while (await reader.ReadAsync(ct))
            items.Add(new(RawSql.Guid(reader, "Id"), RawSql.String(reader, "Kind"),
                RawSql.NullableString(reader, "Label"), Captured(reader), RawSql.Int(reader, "PayloadLength")));
        return items;
    }

    public async Task<FinTsRawResponseDetail?> GetForUserAsync(
        Guid userId, Guid fullWorthSpaceId, Guid connectionId, Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(userId, fullWorthSpaceId, connectionId, ct)) return null;

        var sql = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(sql, """
SELECT "Id","Kind","Label","CapturedAt","PayloadLength","Payload" FROM "FinTsRawResponses"
WHERE "Id"=@id AND "BankConnectionId"=@connection LIMIT 1
""", ("@id", id), ("@connection", connectionId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new(RawSql.Guid(reader, "Id"), RawSql.String(reader, "Kind"),
            RawSql.NullableString(reader, "Label"), Captured(reader), RawSql.Int(reader, "PayloadLength"),
            cipher.Unprotect(RawSql.String(reader, "Payload")) ?? string.Empty);
    }

    private Task<bool> OwnsAsync(Guid userId, Guid fullWorthSpaceId, Guid connectionId, CancellationToken ct) =>
        db.BankConnections.AsNoTracking().AnyAsync(connection =>
            connection.Id == connectionId &&
            connection.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member =>
                member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId), ct);

    private static DateTimeOffset Captured(DbDataReader reader)
    {
        var value = reader.GetFieldValue<DateTime>(reader.GetOrdinal("CapturedAt"));
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}
