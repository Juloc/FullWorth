using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Purchases;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Collections;

/// <summary>Eine Sammlung in der Uebersicht - mit dem, was in ihr steckt.</summary>
public sealed record CollectionRow(
    Guid Id, string Name, string? Description, string? Icon, string? Color,
    DateOnly? StartDate, DateOnly? EndDate, string Status,
    int TransactionCount, decimal Expenses, decimal Income, decimal Net, string Currency,
    bool IsComplete, IReadOnlyList<string> MissingCurrencies);

/// <summary>Was eine Sammlung nach Kategorien aufteilt - der Punkt, an dem beide Achsen zusammenkommen.</summary>
public sealed record CollectionCategoryShare(Guid? CategoryId, string CategoryName, decimal Expenses, int Count);

/// <summary>Ein Kandidat fuer eine Sammlung, mit dem Grund, warum er vorgeschlagen wird.</summary>
public sealed record CollectionCandidate(
    Guid TransactionId, DateOnly? Date, decimal Amount, string Currency,
    string? Counterparty, string? CategoryName, string? AccountName, IReadOnlyList<string> Reasons, int Score);

/// <summary>
/// Die Datenseite der Sammlungen (#124).
///
/// Zwei Regeln stehen ueber allem und sind der Grund, warum hier gerechnet wird und nicht in der
/// Oberflaeche:
///
/// 1. <b>Innerhalb einer Sammlung wird jede Buchung genau einmal gezaehlt.</b> Die Relation hat einen
///    zusammengesetzten Schluessel, also kann dieselbe Buchung nicht zweimal drinstehen - aber ein
///    JOIN ueber mehrere Sammlungen wuerde sie trotzdem vervielfachen. Jede Abfrage hier bleibt
///    deshalb auf EINE Sammlung beschraenkt.
/// 2. <b>Sammlungen ueberschneiden sich, ihre Summen sind nicht addierbar.</b> Eine Bauhaus-Buchung
///    kann zu „Wohnung" und zu „Badrenovierung" gehoeren; beide zeigen 184 EUR, die Gesamtausgaben
///    zeigen weiterhin 184 EUR. Diese Stelle liefert nie eine Gesamtsumme ueber Sammlungen hinweg.
///
/// Fremdwaehrung: gerechnet wird in der Basiswaehrung des Space. Fehlt ein Kurs, wird die Zeile NICHT
/// stillschweigend eins zu eins genommen - die Summe gilt als unvollstaendig und sagt, welche Waehrung
/// fehlt. Dieselbe Regel wie in der Vermoegensuebersicht.
/// </summary>
public sealed class CollectionStore(FullWorthDbContext db)
{
    /// <summary>Mehr Kandidaten sieht sich niemand an, und mehr zu laden kostet nur Zeit.</summary>
    private const int MaxCandidates = 200;

    public Task<string> BaseCurrencyAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId)
            .Select(space => space.BaseCurrency)
            .SingleAsync(ct);

    public Task<FinanceTag?> FindAsync(Guid fullWorthSpaceId, Guid id, CancellationToken ct) =>
        db.Set<FinanceTag>().SingleOrDefaultAsync(
            tag => tag.Id == id && tag.FullWorthSpaceId == fullWorthSpaceId, ct);

    public Task<bool> NameTakenAsync(Guid fullWorthSpaceId, string normalized, Guid? exceptId, CancellationToken ct) =>
        db.Set<FinanceTag>().AsNoTracking().AnyAsync(
            tag => tag.FullWorthSpaceId == fullWorthSpaceId
                   && tag.NormalizedName == normalized
                   && (exceptId == null || tag.Id != exceptId), ct);

    public void Add(FinanceTag tag) => db.Set<FinanceTag>().Add(tag);
    public void Remove(FinanceTag tag) => db.Set<FinanceTag>().Remove(tag);
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    /// <summary>
    /// Die Uebersicht: jede Sammlung mit ihren Kennzahlen, in EINER Abfrage.
    ///
    /// Die Alternative waere eine Abfrage je Sammlung gewesen - bei zwanzig Sammlungen zwanzig Runden
    /// fuer eine Seite. Gruppiert wird nach Sammlung UND Waehrung, damit ein fehlender Kurs
    /// hinterher genau benannt werden kann statt die ganze Summe zu verwerfen.
    /// </summary>
    public async Task<IReadOnlyList<CollectionRow>> ListAsync(
        Guid fullWorthSpaceId, IReadOnlySet<Guid> visibleAccounts, string baseCurrency,
        FxSnapshot fx, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT t."Id"            AS "CollectionId",
       t."Name", t."Description", t."Icon", t."Color",
       t."StartDate", t."EndDate", t."Status",
       x."Currency", x."Day",
       COALESCE(x."Rows", 0)     AS "Rows",
       COALESCE(x."Expenses", 0) AS "Expenses",
       COALESCE(x."Income", 0)   AS "Income"
FROM "FinanceTags" t
LEFT JOIN (
  SELECT tt."TagId",
         tx."Currency",
         COALESCE(tx."BookingDate", tx."ValueDate") AS "Day",
         count(*)                                              AS "Rows",
         COALESCE(sum(CASE WHEN tx."Amount" < 0 THEN -tx."Amount" END), 0) AS "Expenses",
         COALESCE(sum(CASE WHEN tx."Amount" > 0 THEN  tx."Amount" END), 0) AS "Income"
  FROM "TransactionTags" tt
  JOIN "Transactions" tx ON tx."Id" = tt."TransactionId"
  WHERE tx."AccountId" = ANY(@accounts) AND NOT tx."IsIgnored"
  GROUP BY tt."TagId", tx."Currency", COALESCE(tx."BookingDate", tx."ValueDate")
) x ON x."TagId" = t."Id"
WHERE t."FullWorthSpaceId" = @space
ORDER BY t."Name", x."Currency", x."Day";
""", ("@space", fullWorthSpaceId), ("@accounts", visibleAccounts.ToArray()));

        var buffer = new Dictionary<Guid, Builder>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = RawSql.Guid(reader, "CollectionId");
            if (!buffer.TryGetValue(id, out var entry))
                buffer[id] = entry = new Builder(
                    id,
                    RawSql.String(reader, "Name"),
                    RawSql.NullableString(reader, "Description"),
                    RawSql.NullableString(reader, "Icon"),
                    RawSql.NullableString(reader, "Color"),
                    RawSql.NullableDate(reader, "StartDate"),
                    RawSql.NullableDate(reader, "EndDate"),
                    RawSql.String(reader, "Status"));

            var currency = RawSql.NullableString(reader, "Currency");
            if (currency is null) continue;
            entry.Add(currency, RawSql.NullableDate(reader, "Day"), RawSql.Int(reader, "Rows"),
                RawSql.Decimal(reader, "Expenses"), RawSql.Decimal(reader, "Income"), baseCurrency, fx);
        }

        return buffer.Values
            .Select(entry => entry.Build(baseCurrency))
            .OrderBy(row => StatusOrder(row.Status))
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>Aktiv zuerst, dann abgeschlossen, dann archiviert - was laeuft, steht vorn.</summary>
    private static int StatusOrder(string status) => status switch
    {
        CollectionStatuses.Active => 0,
        CollectionStatuses.Completed => 1,
        _ => 2
    };

    /// <summary>
    /// Die Kategorie-Aufteilung einer Sammlung: „Gardasee 2026 - Hotel 680, Restaurant 286, …".
    /// Genau EINE Sammlung, also kann keine Buchung doppelt zaehlen.
    /// </summary>
    public async Task<IReadOnlyList<CollectionCategoryShare>> CategorySplitAsync(
        Guid collectionId, IReadOnlySet<Guid> visibleAccounts, string baseCurrency,
        FxSnapshot fx, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT tx."CategoryId",
       COALESCE(c."Name", '')  AS "CategoryName",
       tx."Currency",
       COALESCE(tx."BookingDate", tx."ValueDate") AS "Day",
       count(*)                AS "Rows",
       COALESCE(sum(CASE WHEN tx."Amount" < 0 THEN -tx."Amount" END), 0) AS "Expenses"
FROM "TransactionTags" tt
JOIN "Transactions" tx ON tx."Id" = tt."TransactionId"
LEFT JOIN "Categories" c ON c."Id" = tx."CategoryId"
WHERE tt."TagId" = @collection AND tx."AccountId" = ANY(@accounts) AND NOT tx."IsIgnored"
GROUP BY tx."CategoryId", c."Name", tx."Currency", COALESCE(tx."BookingDate", tx."ValueDate");
""", ("@collection", collectionId), ("@accounts", visibleAccounts.ToArray()));

        var buffer = new Dictionary<Guid?, (string Name, decimal Expenses, int Count)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var categoryId = RawSql.NullableGuid(reader, "CategoryId");
            var currency = RawSql.String(reader, "Currency");
            var expenses = Convert(RawSql.Decimal(reader, "Expenses"), currency, RawSql.NullableDate(reader, "Day"), baseCurrency, fx);
            if (expenses is null) continue;

            var name = RawSql.String(reader, "CategoryName");
            var count = RawSql.Int(reader, "Rows");
            if (buffer.TryGetValue(categoryId, out var existing))
                buffer[categoryId] = (existing.Name, existing.Expenses + expenses.Value, existing.Count + count);
            else
                buffer[categoryId] = (name, expenses.Value, count);
        }

        return buffer
            .Select(pair => new CollectionCategoryShare(pair.Key, pair.Value.Name, pair.Value.Expenses, pair.Value.Count))
            .OrderByDescending(share => share.Expenses)
            .ToArray();
    }

    public async Task<IReadOnlyList<Guid>> TransactionIdsAsync(
        Guid collectionId, IReadOnlySet<Guid> visibleAccounts, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT tt."TransactionId"
FROM "TransactionTags" tt
JOIN "Transactions" tx ON tx."Id" = tt."TransactionId"
WHERE tt."TagId" = @collection AND tx."AccountId" = ANY(@accounts);
""", ("@collection", collectionId), ("@accounts", visibleAccounts.ToArray()));

        var result = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0));
        return result;
    }

    /// <summary>
    /// Buchungen zuordnen - alle auf einmal.
    ///
    /// <c>unnest</c> statt einer Schleife: 100 markierte Buchungen sind eine Anweisung, nicht 100.
    /// <c>ON CONFLICT DO NOTHING</c> macht das Zuordnen wiederholbar, und es ist genau der Grund,
    /// warum „hinzufuegen" bestehende Zuordnungen nicht ersetzt.
    /// Nur Buchungen aus schreibbaren Konten - der Aufrufer hat die Liste schon gefiltert.
    /// </summary>
    public async Task<int> AddAsync(
        Guid collectionId, IReadOnlyCollection<Guid> transactionIds, string source, CancellationToken ct)
    {
        if (transactionIds.Count == 0) return 0;
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
INSERT INTO "TransactionTags" ("TransactionId", "TagId", "CreatedAt")
SELECT id, @collection, @now FROM unnest(@ids) AS id
ON CONFLICT ("TransactionId", "TagId") DO NOTHING;
""", ("@collection", collectionId), ("@ids", transactionIds.ToArray()), ("@now", DateTimeOffset.UtcNow));
        _ = source;
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> RemoveAsync(
        Guid collectionId, IReadOnlyCollection<Guid> transactionIds, CancellationToken ct)
    {
        if (transactionIds.Count == 0) return 0;
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
DELETE FROM "TransactionTags" WHERE "TagId" = @collection AND "TransactionId" = ANY(@ids);
""", ("@collection", collectionId), ("@ids", transactionIds.ToArray()));
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Die Sammlungen EINER Buchung - fuer die Chips in der Liste und im Detail.</summary>
    public async Task<Dictionary<Guid, List<Guid>>> CollectionsOfAsync(
        IReadOnlyCollection<Guid> transactionIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, List<Guid>>();
        if (transactionIds.Count == 0) return result;

        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            """SELECT "TransactionId", "TagId" FROM "TransactionTags" WHERE "TransactionId" = ANY(@ids);""",
            ("@ids", transactionIds.ToArray()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var transactionId = reader.GetGuid(0);
            if (!result.TryGetValue(transactionId, out var list)) result[transactionId] = list = [];
            list.Add(reader.GetGuid(1));
        }
        return result;
    }

    /// <summary>
    /// Vorschlaege - deterministisch, ohne KI und ohne einen einzigen Schreibvorgang.
    ///
    /// Der Zeitraum ALLEIN reicht ausdruecklich nicht: waehrend einer Reise laeuft die Miete weiter.
    /// Deshalb zaehlt hier jeder Grund einzeln, und die Gruende stehen im Ergebnis, damit der
    /// Benutzer die Auswahl beurteilen kann statt ihr zu glauben:
    ///
    ///   +3  ein Haendler, der in dieser Sammlung schon vorkommt
    ///   +2  eine Kategorie, die in dieser Sammlung schon vorkommt
    ///   +2  innerhalb des Zeitraums der Sammlung
    ///   +1  aehnlicher Buchungstext wie eine bereits zugeordnete Buchung
    ///
    /// Ohne Zeitraum und ohne eine einzige Zuordnung gibt es keinen Grund - dann schlaegt diese
    /// Stelle nichts vor, statt irgendetwas vorzuschlagen.
    /// </summary>
    public async Task<IReadOnlyList<CollectionCandidate>> CandidatesAsync(
        Guid fullWorthSpaceId, Guid collectionId, DateOnly? start, DateOnly? end,
        IReadOnlySet<Guid> visibleAccounts, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
WITH assigned AS (
  SELECT tx."NormalizedCounterparty" AS "Party", tx."CategoryId"
  FROM "TransactionTags" tt
  JOIN "Transactions" tx ON tx."Id" = tt."TransactionId"
  WHERE tt."TagId" = @collection
),
parties AS (SELECT DISTINCT "Party" FROM assigned WHERE "Party" IS NOT NULL),
cats    AS (SELECT DISTINCT "CategoryId" FROM assigned WHERE "CategoryId" IS NOT NULL)
SELECT tx."Id", tx."BookingDate", tx."Amount", tx."Currency", tx."Counterparty",
       c."Name" AS "CategoryName", a."DisplayName" AS "AccountName",
       (tx."NormalizedCounterparty" IS NOT NULL
        AND tx."NormalizedCounterparty" IN (SELECT "Party" FROM parties))            AS "PartyHit",
       (tx."CategoryId" IS NOT NULL
        AND tx."CategoryId" IN (SELECT "CategoryId" FROM cats))                      AS "CategoryHit",
       (@start IS NOT NULL AND @end IS NOT NULL
        AND tx."BookingDate" BETWEEN @start AND @end)                                AS "PeriodHit"
FROM "Transactions" tx
JOIN "Accounts" a ON a."Id" = tx."AccountId"
LEFT JOIN "Categories" c ON c."Id" = tx."CategoryId"
WHERE a."FullWorthSpaceId" = @space
  AND tx."AccountId" = ANY(@accounts)
  AND NOT tx."IsIgnored"
  AND NOT tx."IsTransfer"
  AND NOT EXISTS (SELECT 1 FROM "TransactionTags" x
                  WHERE x."TransactionId" = tx."Id" AND x."TagId" = @collection)
  AND (
        (tx."NormalizedCounterparty" IS NOT NULL AND tx."NormalizedCounterparty" IN (SELECT "Party" FROM parties))
     OR (tx."CategoryId" IS NOT NULL AND tx."CategoryId" IN (SELECT "CategoryId" FROM cats))
     OR (@start IS NOT NULL AND @end IS NOT NULL AND tx."BookingDate" BETWEEN @start AND @end)
      )
ORDER BY tx."BookingDate" DESC NULLS LAST
LIMIT @limit;
""",
            ("@space", fullWorthSpaceId), ("@collection", collectionId),
            ("@accounts", visibleAccounts.ToArray()),
            ("@start", start), ("@end", end), ("@limit", MaxCandidates));

        var result = new List<CollectionCandidate>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ToCandidate(reader));

        return result.OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Date)
            .ToArray();
    }

    private static CollectionCandidate ToCandidate(DbDataReader reader)
    {
        var reasons = new List<string>();
        var score = 0;
        if (RawSql.Bool(reader, "PartyHit")) { reasons.Add("merchant"); score += 3; }
        if (RawSql.Bool(reader, "CategoryHit")) { reasons.Add("category"); score += 2; }
        if (RawSql.Bool(reader, "PeriodHit")) { reasons.Add("period"); score += 2; }

        return new CollectionCandidate(
            RawSql.Guid(reader, "Id"),
            RawSql.NullableDate(reader, "BookingDate"),
            RawSql.Decimal(reader, "Amount"),
            RawSql.String(reader, "Currency"),
            RawSql.NullableString(reader, "Counterparty"),
            RawSql.NullableString(reader, "CategoryName"),
            RawSql.NullableString(reader, "AccountName"),
            reasons,
            score);
    }

    /// <summary>
    /// In die Basiswaehrung, oder gar nicht. Ein fehlender Kurs ergibt null, und der Aufrufer macht
    /// daraus „unvollstaendig" - niemals eins zu eins und niemals null Euro.
    /// </summary>
    private static decimal? Convert(
        decimal amount, string currency, DateOnly? day, string baseCurrency, FxSnapshot fx)
    {
        if (string.Equals(currency, baseCurrency, StringComparison.OrdinalIgnoreCase)) return amount;
        // Ohne Datum gibt es keinen Kurs, der zu dieser Zeile gehoert - und der heutige waere eine
        // Erfindung. Unvollstaendig ist die richtige Antwort.
        return day is { } date ? fx.ToBaseOn(amount, currency, date) : null;
    }

    private sealed class Builder(
        Guid id, string name, string? description, string? icon, string? color,
        DateOnly? start, DateOnly? end, string status)
    {
        private readonly SortedSet<string> missing = new(StringComparer.Ordinal);
        private int count;
        private decimal expenses;
        private decimal income;

        public void Add(
            string currency, DateOnly? day, int rows, decimal rawExpenses, decimal rawIncome,
            string baseCurrency, FxSnapshot fx)
        {
            count += rows;
            var convertedExpenses = Convert(rawExpenses, currency, day, baseCurrency, fx);
            var convertedIncome = Convert(rawIncome, currency, day, baseCurrency, fx);
            if (convertedExpenses is null || convertedIncome is null)
            {
                missing.Add(currency.ToUpperInvariant());
                return;
            }
            expenses += convertedExpenses.Value;
            income += convertedIncome.Value;
        }

        public CollectionRow Build(string baseCurrency) => new(
            id, name, description, icon, color, start, end, status,
            count, expenses, income, income - expenses, baseCurrency,
            missing.Count == 0, missing.ToArray());
    }
}
