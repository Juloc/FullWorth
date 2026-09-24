using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

/// <summary>
/// Beim Zusammenfuehren zweier Buchungen wird eine davon geloescht. Alles, was auf sie zeigt, muss
/// vorher auf die andere umgehaengt werden - sonst nimmt eine CASCADE-Regel die Arbeit des Nutzers
/// mit (Schlagworte, Pruefzustaende, Vertragszuordnung) oder eine RESTRICT-Regel laesst die ganze
/// Zusammenfuehrung platzen.
///
/// Die Liste in <see cref="TransactionMergeService"/> ist von Hand geschrieben, also rottet sie. Das
/// hier haelt sie gegen das laufende Schema - dasselbe Verfahren wie
/// <see cref="ImportTransactionProvenanceGuardTests"/>, nur fuer die andere Haelfte des Problems.
/// </summary>
public sealed class TransactionMergeGuardTests
{
    [Fact]
    public async Task MergeMovesEveryTableThatPointsAtATransaction()
    {
        var referencing = new List<(string Table, string Column)>();
        using var factory = new BackendWebApplicationFactory();
        await factory.SeedAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT c.conrelid::regclass::text AS tbl,
       (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
          FROM unnest(c.conkey) WITH ORDINALITY k(attnum, ord)
          JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum) AS cols
FROM pg_constraint c
WHERE c.contype='f'
  AND c.confrelid=(SELECT oid FROM pg_class WHERE relname='Transactions' AND relkind='r' LIMIT 1)
""";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                referencing.Add((reader.GetString(0).Trim('"'), reader.GetString(1)));
        });

        Assert.NotEmpty(referencing);
        var covered = TransactionMergeService.CoveredTables;

        // Die Gegenrichtung: eine Tabelle, die es gar nicht gibt, wuerde beim Umhaengen nur eine
        // Ausnahme werfen - und ein Tippfehler im Namen faellt sonst nirgends auf.
        var known = referencing.Select(entry => entry.Table).ToHashSet(StringComparer.Ordinal);
        foreach (var table in covered)
            Assert.True(
                known.Contains(table),
                $"TransactionMergeService nennt \"{table}\", aber diese Tabelle hat gar keinen "
                + "Fremdschluessel auf \"Transactions\". Name veraltet oder Tabelle entfallen?");

        foreach (var (table, column) in referencing)
        {
            // Der Herkunftsnachweis wandert bewusst NICHT mit: er sagt "dieser Import hat diese
            // Buchung erzeugt", und der Rollback loescht daran entlang. Auf einer Bankbuchung wuerde
            // er eine echte Bankbuchung loeschbar machen.
            if (table == "ImportTransactionLinks") continue;

            Assert.True(
                covered.Contains(table),
                $"\"{table}\".\"{column}\" ist ein Fremdschluessel auf \"Transactions\", aber "
                + "TransactionMergeService haengt die Tabelle beim Zusammenfuehren nicht um. Entscheide "
                + "bewusst: entweder sie wandert mit (Eintrag in Dependents, samt Eindeutigkeits-"
                + "Schluessel), oder sie darf verschwinden (dann hier ausnehmen und begruenden).");
        }
    }

    /// <summary>
    /// Die Umhaeng-Liste und die Rollback-Schutzliste beschreiben dieselbe Menge Tabellen aus zwei
    /// Richtungen. Laufen sie auseinander, schuetzt der Rollback etwas, das die Zusammenfuehrung
    /// stillschweigend wegwirft - oder umgekehrt.
    /// </summary>
    [Fact]
    public void MergeListAndRollbackListDescribeTheSameTables()
    {
        var rollbackSql = ImportTransactionProvenance.DeleteImportedTransactionsSql;
        foreach (var table in TransactionMergeService.CoveredTables)
        {
            // Die eine Tabelle, bei der die beiden Listen mit Absicht auseinandergehen (#131): eine
            // Ergaenzungs-Notiz wandert beim Zusammenfuehren mit, weil die Aufteilungen, die sie
            // beschreibt, mitwandern - aber sie darf die Ruecknahme des Imports, der die Buchung
            // ERZEUGT hat, nicht blockieren. Sie ist keine Nutzerarbeit, sondern die Notiz einer
            // zweiten Quelle, und eine Notiz ueber eine geloeschte Buchung beschreibt nichts mehr.
            // Zurueckgenommen wird sie auf dem anderen Weg, in
            // ImportTransactionEnrichment.RevertAsync.
            if (table == "ImportTransactionEnrichments") continue;

            // "Transactions" steht im Rollback-SQL als die Tabelle, aus der geloescht wird - der
            // Selbstverweis "RefundOfTransactionId" ist dort eine eigene NOT-EXISTS-Zeile.
            Assert.True(
                rollbackSql.Contains($"\"{table}\"", StringComparison.Ordinal),
                $"\"{table}\" wird beim Zusammenfuehren umgehaengt, aber der Import-Rollback kennt sie "
                + "nicht. Eine der beiden Listen ist veraltet.");
        }
    }
}
