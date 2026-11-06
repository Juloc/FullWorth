namespace FullWorth.Backend.Modules.Ingestion;

/// <summary>
/// Eine vorgemerkte Zeile, die durch ihre Buchung ersetzt wurde - und die die Bank trotzdem weiter liefert.
///
/// Die Bank meldet eine vorgemerkte Kartenzahlung nach der Buchung weiter als vorgemerkt (gemessen an den
/// echten Daten), und ihr Schluessel enthaelt den Status: die Buchung kommt als neue Zeile, die
/// Vormerkung bleibt daneben stehen. Loescht man sie nur, legt der naechste Abruf sie wieder an. Dieser
/// Eintrag merkt sich deshalb den Schluessel, damit ihn der Abruf ueberspringt. Er sagt nur "diese
/// Vormerkung nicht noch einmal anlegen", mehr nicht.
/// </summary>
public sealed class SupersededPendingTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public Guid AccountId { get; set; }
    public string ExternalKey { get; set; } = string.Empty;
    public DateTimeOffset SupersededAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record PendingCandidate(
    Guid Id, decimal Amount, string Currency, DateOnly Date, string? Counterparty, DateTimeOffset FirstSeenAt);

public sealed record BookedCandidate(
    Guid Id, decimal Amount, string Currency, DateOnly Date, string? Counterparty, DateTimeOffset FirstSeenAt);

/// <summary>
/// Welche Vormerkung durch welche Buchung abgeloest wurde. Reine Entscheidung ohne Datenbank - sie
/// entscheidet, ob eine echte Zahlung verschwindet, und muss deshalb ohne Aufbau pruefbar sein.
///
/// Im Zweifel bleibt ein Doppeltes stehen: eine uebersehene Vormerkung ist ein Schoenheitsfehler, eine
/// zu Unrecht entfernte ist eine fehlende Zahlung. Darum muss ALLES zutreffen:
///
/// <list type="bullet">
/// <item>gleicher Betrag in gleicher Waehrung - bis auf den Cent,</item>
/// <item>die Buchung liegt kurz nach dem Zahltag der Vormerkung (Banken buchen Kartenzahlungen mit ein
/// paar Tagen Verzug), nie davor,</item>
/// <item>dieselbe Gegenpartei - das erste Wort des normalisierten Namens, mindestens drei Zeichen; leer
/// heisst "unklar" und passt nie,</item>
/// <item>die Buchung wurde NICHT vor der Vormerkung zum ersten Mal gesehen: eine Buchung, die es schon gab,
/// kann nicht die Aufloesung einer Vormerkung sein, die erst danach aufgetaucht ist (der Kaffee von
/// gestern und der von heute).</item>
/// </list>
///
/// Eine Buchung ersetzt hoechstens eine Vormerkung; zwei gleiche Penny-Zahlungen am selben Tag
/// brauchen zwei Buchungen. Die aelteste Vormerkung waehlt zuerst, dann die naechstliegende Buchung -
/// jeweils mit der Id als letztem Kriterium, damit zwei Laeufe dasselbe entscheiden.
/// </summary>
public static class PendingSupersession
{
    private const int DaysBefore = 1;
    private const int DaysAfter = 10;
    private const int MinimumWordLength = 3;

    public static IReadOnlyList<(Guid PendingId, Guid BookedId)> Pair(
        IReadOnlyList<PendingCandidate> pending,
        IReadOnlyList<BookedCandidate> booked)
    {
        var claimed = new HashSet<Guid>();
        var pairs = new List<(Guid, Guid)>();

        foreach (var candidate in pending.OrderBy(x => x.Date).ThenBy(x => x.FirstSeenAt).ThenBy(x => x.Id))
        {
            var match = booked
                .Where(x => !claimed.Contains(x.Id) && Matches(candidate, x))
                .OrderBy(x => Math.Abs(x.Date.DayNumber - candidate.Date.DayNumber))
                .ThenBy(x => x.FirstSeenAt)
                .ThenBy(x => x.Id)
                .FirstOrDefault();
            if (match is null) continue;

            claimed.Add(match.Id);
            pairs.Add((candidate.Id, match.Id));
        }

        return pairs;
    }

    private static bool Matches(PendingCandidate pending, BookedCandidate booked)
    {
        if (pending.Amount != booked.Amount) return false;
        if (!string.Equals(pending.Currency, booked.Currency, StringComparison.OrdinalIgnoreCase)) return false;

        var distance = booked.Date.DayNumber - pending.Date.DayNumber;
        if (distance < -DaysBefore || distance > DaysAfter) return false;

        if (booked.FirstSeenAt < pending.FirstSeenAt) return false;

        var pendingWord = FirstWord(pending.Counterparty);
        return pendingWord is not null && string.Equals(pendingWord, FirstWord(booked.Counterparty), StringComparison.Ordinal);
    }

    private static string? FirstWord(string? normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var word = normalized.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return word.Length >= MinimumWordLength ? word : null;
    }
}
