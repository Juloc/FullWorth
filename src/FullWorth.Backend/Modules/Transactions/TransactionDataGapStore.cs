using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

/// <summary>
/// Eine auffaellige Luecke in der Buchungshistorie eines Kontos: von der letzten Buchung davor bis zur
/// ersten danach. <see cref="Days"/> ist der Abstand in Tagen, nicht die Zahl der fehlenden Tage - was
/// dazwischen fehlt, weiss niemand, und genau das ist die Aussage.
/// </summary>
public sealed record TransactionDataGap(Guid AccountId, DateOnly From, DateOnly To, int Days);

/// <summary>
/// Findet Luecken in der Buchungshistorie (#131, Abschnitt 13).
///
/// Eine Luecke ist ein HINWEIS, keine Behauptung: moegliche Ursachen sind eine zeitweise getrennte
/// Bankverbindung, die begrenzte Reichweite einer API oder ein Dateiexport, der den Zeitraum nicht
/// abdeckt. Es wird nichts erfunden und nichts nachgetragen.
///
/// Drei Entscheidungen, die den Hinweis brauchbar statt laestig machen:
///
/// <list type="number">
///   <item><b>Gemessen am eigenen Rhythmus des Kontos.</b> Ein Konto mit zwei Buchungen im Monat hat
///         normalerweise zwei Wochen Abstand - das ist dort keine Luecke. Verglichen wird deshalb
///         gegen den Median der Abstaende DIESES Kontos, nicht gegen eine feste Zahl.</item>
///   <item><b>Nur im Inneren.</b> Vor der ersten und nach der letzten Buchung ist keine Luecke,
///         sondern der Rand der Daten. Dass ein Konto seit drei Wochen nichts mehr liefert, ist eine
///         andere Aussage und steht als Datenstand am Konto.</item>
///   <item><b>Erst ab genug Historie.</b> Aus fuenf Buchungen laesst sich kein Rhythmus ablesen; ein
///         Hinweis daraus waere geraten.</item>
/// </list>
///
/// Gespeichert wird nichts. Der Hinweis ergibt sich jedes Mal aus den Buchungen selbst und
/// verschwindet damit von allein, sobald ein Import die Luecke schliesst.
/// </summary>
public sealed class TransactionDataGapStore(FullWorthDbContext db)
{
    /// <summary>Kuerzer nennt niemand eine Luecke - ein langes Wochenende mit Feiertagen reicht dafuer aus.</summary>
    private const int MinimumDays = 10;

    /// <summary>So viel Mal der uebliche Abstand dieses Kontos muss es mindestens sein.</summary>
    private const int Factor = 5;

    /// <summary>Unter so vielen Abstaenden gibt es keinen Rhythmus, gegen den sich etwas messen liesse.</summary>
    private const int MinimumSteps = 12;

    public async Task<IReadOnlyList<TransactionDataGap>> FindForUserAsync(
        Guid userId, Guid? fullWorthSpaceId, Guid? accountId, CancellationToken ct)
    {
        // Berechtigung zuerst, und zwar als Liste von Konten-Ids: die eigentliche Abfrage laeuft danach
        // ueber genau diese und kann keinen fremden Bestand streifen.
        var accounts = await db.Accounts.AsNoTracking()
            .Where(account =>
                (!accountId.HasValue || account.Id == accountId.Value) &&
                (!fullWorthSpaceId.HasValue || account.FullWorthSpaceId == fullWorthSpaceId.Value) &&
                db.FullWorthSpaceMembers.Any(member =>
                    member.FullWorthSpaceId == account.FullWorthSpaceId && member.UserId == userId) &&
                account.Owners.Any(owner => owner.UserId == userId))
            .Select(account => account.Id)
            .ToArrayAsync(ct);
        if (accounts.Length == 0) return [];

        // Fensterfunktionen und der Median gehoeren in die Datenbank: beides in C# hiesse, jede
        // Buchung jedes Kontos zu laden, um am Ende ein paar Zeilen zu behalten.
        var rows = await db.Database.SqlQuery<GapRow>($"""
            WITH days AS (
                SELECT DISTINCT "AccountId" AS account_id, COALESCE("BookingDate", "ValueDate") AS day
                FROM "Transactions"
                WHERE "AccountId" = ANY({accounts})
                  AND "IsIgnored" = false
                  AND COALESCE("BookingDate", "ValueDate") IS NOT NULL
            ),
            steps AS (
                SELECT account_id,
                       lag(day) OVER (PARTITION BY account_id ORDER BY day) AS previous_day,
                       day
                FROM days
            ),
            measured AS (
                SELECT account_id, previous_day, day, (day - previous_day) AS length
                FROM steps
                WHERE previous_day IS NOT NULL
            ),
            rhythm AS (
                SELECT account_id,
                       count(*) AS step_count,
                       percentile_cont(0.5) WITHIN GROUP (ORDER BY length) AS median_length
                FROM measured
                GROUP BY account_id
            )
            SELECT measured.account_id AS "AccountId",
                   measured.previous_day AS "From",
                   measured.day AS "To",
                   measured.length AS "Days"
            FROM measured
            JOIN rhythm ON rhythm.account_id = measured.account_id
            WHERE rhythm.step_count >= {MinimumSteps}
              AND measured.length >= {MinimumDays}
              AND measured.length >= {Factor} * GREATEST(rhythm.median_length, 1)
            ORDER BY measured.account_id, measured.length DESC, measured.previous_day
            """).ToListAsync(ct);

        return rows.Select(row => new TransactionDataGap(row.AccountId, row.From, row.To, row.Days)).ToArray();
    }

    private sealed record GapRow(Guid AccountId, DateOnly From, DateOnly To, int Days);
}
