using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Haengt alles, was auf eine Buchung zeigt, auf eine andere um - damit die Verliererzeile einer
/// Zusammenfuehrung geloescht werden kann, ohne die Arbeit des Nutzers mitzunehmen.
///
/// Vorher hing die Zusammenfuehrung vier Beziehungen um (Splits, Kaeufe, Erstattungs-Rueckverweis,
/// Preisbelege) und loeschte dann die Zeile. Sechzehn Fremdschluessel zeigen auf "Transactions", und
/// der Rest verhielt sich nach seiner eigenen Regel: "TransactionTags", "SpendingReviews",
/// "TransactionReviewStates", "ContractTransactionLinks" und "RefundSuggestionDismissals" fielen per
/// CASCADE weg - der Nutzer verlor seine Schlagworte, seine Pruefzustaende und seine Vertragszuordnung,
/// ohne dass irgendwo etwas davon stand. "AssetCashflowEntries" und "PurchasePaymentLinks" stehen auf
/// RESTRICT und liessen die Zusammenfuehrung stattdessen mit einer Fremdschluesselverletzung platzen.
///
/// Die Liste unten ist dieselbe wie in <see cref="ImportTransactionProvenance"/> und aus demselben
/// Grund gefaehrlich: sie ist von Hand geschrieben. <c>TransactionMergeGuardTests</c> haelt sie gegen
/// das laufende Schema, damit eine neue Tabelle nicht still durchfaellt.
/// </summary>
internal static class TransactionMergeService
{
    /// <param name="Table">Tabelle, die auf eine Buchung zeigt.</param>
    /// <param name="Column">Die Spalte mit dem Fremdschluessel.</param>
    /// <param name="ConflictWith">
    /// <c>null</c>, wenn kein Eindeutigkeits-Schluessel <paramref name="Column"/> enthaelt. Sonst die
    /// UEBRIGEN Spalten dieses Schluessels - leer, wenn die Spalte fuer sich allein eindeutig ist.
    /// PostgreSQL kennt kein ON CONFLICT beim UPDATE, also muss eine Zeile, die beim Umhaengen mit
    /// einer schon vorhandenen des Gewinners kollidieren wuerde, vorher weg.
    /// </param>
    /// <param name="SelfReference">
    /// Die Tabelle IST "Transactions": der Gewinner darf nach dem Umhaengen nicht auf sich selbst
    /// zeigen. Nur hier gibt es diesen Fall, und nur hier ist "Id" die Buchungskennung.
    /// </param>
    private sealed record Dependent(
        string Table, string Column, string[]? ConflictWith, bool SelfReference = false);

    /// <summary>
    /// Jeder Fremdschluessel auf "Transactions", ausser <c>ImportTransactionLinks</c> - siehe
    /// <see cref="MoveDependenciesAsync"/> - und <c>RefundSuggestionDismissals</c>, das zwei davon
    /// traegt und darum unten eigens behandelt wird.
    /// </summary>
    private static readonly Dependent[] Dependents =
    [
        new("AssetCashflowEntries", "TransactionId", ["AssetId", "Type"]),
        new("ContractTransactionLinks", "TransactionId", ["ContractId"]),
        new("PriceChangeSuggestions", "EvidenceTransactionId", null),
        new("PurchaseItemReturns", "RefundTransactionId", null),
        new("PurchasePaymentLinks", "TransactionId", ["PurchaseId"]),
        new("PurchaseRefunds", "TransactionId", []),
        new("Purchases", "TransactionId", null),
        new("ReceivablePayments", "TransactionId", ["AssetId"]),
        new("SpendingReviews", "TransactionId", ["FullWorthSpaceId", "UserId"]),
        new("TransactionAllocations", "TransactionId", null),
        new("TransactionReviewStates", "TransactionId", []),
        new("TransactionTags", "TransactionId", ["TagId"]),
        new("Transactions", "RefundOfTransactionId", null, SelfReference: true)
    ];

    /// <summary>Die Tabellen, die diese Liste abdeckt - fuer den Schema-Abgleich im Test.</summary>
    public static IReadOnlyCollection<string> CoveredTables { get; } =
        [.. Dependents.Select(dependent => dependent.Table), "RefundSuggestionDismissals"];

    /// <summary>
    /// Duerfen die beiden Zeilen ueberhaupt zusammengefuehrt werden? Nein, wenn BEIDE eine Aufteilung
    /// tragen: "TransactionAllocations" hat keinen Eindeutigkeits-Schluessel ueber die Buchung, ein
    /// Umhaengen wuerde die Teilbetraege also addieren und der Gewinner haette doppelt so viel
    /// aufgeteilt, wie er wert ist. Die Aufteilung des Verlierers wegzuwerfen ist keine Alternative -
    /// sie ist Handarbeit. In dem Fall bleiben beide Zeilen stehen.
    /// </summary>
    public static async Task<bool> CanMergeAsync(
        FullWorthDbContext db, Guid loser, Guid winner, CancellationToken ct)
    {
        if (loser == winner) return false;
        var loserHasSplits = await db.TransactionAllocations.AsNoTracking()
            .AnyAsync(allocation => allocation.TransactionId == loser, ct);
        if (!loserHasSplits) return true;
        var winnerHasSplits = await db.TransactionAllocations.AsNoTracking()
            .AnyAsync(allocation => allocation.TransactionId == winner, ct);
        return !winnerHasSplits;
    }

    /// <summary>
    /// Eine Buchungskennung so, wie der jeweilige Anbieter sie in der Spalte stehen hat.
    ///
    /// Auf PostgreSQL ist das eine <c>uuid</c> und eine Guid als Parameter genau richtig. Auf dem
    /// SQLite, mit dem ein Teil der Tests laeuft, schreibt EF die Kennung als TEXT in
    /// GROSSBUCHSTABEN, waehrend Microsoft.Data.Sqlite eine Guid als BLOB binden wuerde und
    /// <c>Guid.ToString()</c> kleinschreibt. Beide Abweichungen enden gleich: der Vergleich traefe
    /// nie zu, und das Umhaengen waere ein stiller Leerlauf statt eines Fehlers - genau die Sorte
    /// Luecke, die einen Test gruen laesst, ohne dass er etwas geprueft haette. (Nachgemessen, nicht
    /// vermutet: EF legt dort "89B08EAB-..." ab.)
    /// </summary>
    private static object Key(FullWorthDbContext db, Guid id) =>
        db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite"
            ? id.ToString().ToUpperInvariant()
            : id;

    /// <summary>
    /// Haengt jede Zeile, die auf <paramref name="loser"/> zeigt, auf <paramref name="winner"/> um.
    /// Danach kann der Verlierer geloescht werden, ohne dass eine CASCADE-Regel etwas mitnimmt.
    ///
    /// <c>ImportTransactionLinks</c> wandert BEWUSST nicht mit. Der Herkunftsnachweis sagt "diese
    /// Buchung hat dieser Import erzeugt", und der Rollback loescht daran entlang. Wanderte er auf eine
    /// Bankbuchung, wuerde eine spaetere Ruecknahme des Imports eine echte Bankbuchung loeschen. Die
    /// zusammengefuehrte Importzeile existiert nicht mehr als eigenes Ding - ihr Nachweis faellt per
    /// CASCADE weg, und das ist die richtige Aussage.
    /// </summary>
    public static async Task MoveDependenciesAsync(
        FullWorthDbContext db, Guid loser, Guid winner, CancellationToken ct)
    {
        if (loser == winner) return;
        var connection = await RawSql.OpenAsync(db, ct);
        var loserKey = Key(db, loser);
        var winnerKey = Key(db, winner);

        foreach (var dependent in Dependents)
        {
            // Erst die Zeilen wegnehmen, die beim Umhaengen mit einer vorhandenen des Gewinners
            // kollidieren wuerden. Der Gewinner hat sie bereits - seine Fassung gilt.
            if (dependent.ConflictWith is not null)
            {
                // Bewusst ohne Alias auf der geloeschten Tabelle, ohne USING und ohne
                // IS NOT DISTINCT FROM: dieselbe Anweisung laeuft so auch auf dem SQLite, mit dem ein
                // Teil der Tests arbeitet.
                var same = string.Concat(dependent.ConflictWith.Select(column =>
                    $""" AND (w."{column}"="{dependent.Table}"."{column}" """
                    + $"""OR (w."{column}" IS NULL AND "{dependent.Table}"."{column}" IS NULL))"""));
                await using var collisions = RawSql.Command(connection, $"""
DELETE FROM "{dependent.Table}"
WHERE "{dependent.Column}"=@loser
  AND EXISTS (SELECT 1 FROM "{dependent.Table}" w
              WHERE w."{dependent.Column}"=@winner{same})
""", ("@loser", loserKey), ("@winner", winnerKey));
                await collisions.ExecuteNonQueryAsync(ct);
            }

            var notItself = dependent.SelfReference ? " AND \"Id\" <> @winner" : string.Empty;
            await using var move = RawSql.Command(connection, $"""
UPDATE "{dependent.Table}" SET "{dependent.Column}"=@winner
WHERE "{dependent.Column}"=@loser{notItself}
""", ("@loser", loserKey), ("@winner", winnerKey));
            await move.ExecuteNonQueryAsync(ct);
        }

        // Zwei Fremdschluessel, beide im Primaerschluessel. Drei Faelle, in dieser Reihenfolge:
        // die Zeile wuerde nach dem Umhaengen auf sich selbst zeigen ("diese Erstattung gehoert nicht
        // zu dieser Buchung" ueber ein und dieselbe Zeile sagt nichts), sie wuerde mit einer
        // vorhandenen kollidieren, sonst wandert sie.
        await using var dismissals = RawSql.Command(connection, """
DELETE FROM "RefundSuggestionDismissals"
WHERE ("RefundTransactionId"=@loser AND "OriginalTransactionId"=@winner)
   OR ("OriginalTransactionId"=@loser AND "RefundTransactionId"=@winner);
DELETE FROM "RefundSuggestionDismissals"
WHERE "RefundTransactionId"=@loser
  AND EXISTS (SELECT 1 FROM "RefundSuggestionDismissals" w
              WHERE w."RefundTransactionId"=@winner
                AND w."OriginalTransactionId"="RefundSuggestionDismissals"."OriginalTransactionId");
DELETE FROM "RefundSuggestionDismissals"
WHERE "OriginalTransactionId"=@loser
  AND EXISTS (SELECT 1 FROM "RefundSuggestionDismissals" w
              WHERE w."OriginalTransactionId"=@winner
                AND w."RefundTransactionId"="RefundSuggestionDismissals"."RefundTransactionId");
UPDATE "RefundSuggestionDismissals" SET "RefundTransactionId"=@winner WHERE "RefundTransactionId"=@loser;
UPDATE "RefundSuggestionDismissals" SET "OriginalTransactionId"=@winner WHERE "OriginalTransactionId"=@loser;
""", ("@loser", loserKey), ("@winner", winnerKey));
        await dismissals.ExecuteNonQueryAsync(ct);

        DetachStaleDependents(db, loser);
    }

    /// <summary>
    /// Das Umhaengen lief in rohem SQL, EF weiss also nichts davon. Haelt der Aenderungsverfolger noch
    /// eine Zeile mit dem alten Verweis, passieren zwei Dinge: sie liest sich veraltet, und - viel
    /// schlimmer - beim Entfernen der Verliererzeile loescht EF sie als abhaengige Zeile gleich mit.
    /// Genau dieser Verlust ist der Grund, warum es diesen Dienst gibt. Also raus aus der Verfolgung;
    /// wer sie danach braucht, liest sie frisch.
    ///
    /// Die Buchungen selbst bleiben verfolgt: die Verliererzeile muss noch geloescht werden koennen.
    /// </summary>
    private static void DetachStaleDependents(FullWorthDbContext db, Guid loser)
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is FinanceTransaction) continue;
            var pointsAtLoser = entry.Metadata.GetForeignKeys()
                .Where(key => key.PrincipalEntityType.ClrType == typeof(FinanceTransaction))
                .SelectMany(key => key.Properties)
                .Any(property => Equals(entry.Property(property.Name).CurrentValue, loser));
            if (pointsAtLoser) entry.State = EntityState.Detached;
        }
    }
}
