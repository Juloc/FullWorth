using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Reconciliation;

/// <summary>
/// Eine Buchung des Girokontos, wie sie für den Abgleich gebraucht wird - absichtlich ein eigener,
/// kleiner Typ statt der echten <c>Transaction</c>-Entität. Ein späterer Slice liest die echten Zeilen
/// aus der Datenbank und baut daraus diesen Datensatz; dieser Matcher selbst darf die Datenbank nicht
/// kennen, sonst ist er nicht mehr in Millisekunden testbar.
/// </summary>
public sealed record BookingCandidate(
    Guid TransactionId,
    DateOnly Date,
    decimal Amount,
    string Currency,
    string? Description,
    bool IsIgnored,
    bool IsTransfer);

/// <summary>Ein Wertpapier, wie es für den Abgleich gebraucht wird - ISIN und/oder WKN als Anker.</summary>
public sealed record SecurityCandidate(
    Guid SecurityId,
    string? Isin,
    string? Wkn,
    string Name);

/// <summary>
/// Eine Buchung, die einem Wertpapier zugeordnet werden konnte. <c>Quantity</c> ist null, wenn am
/// Buchungsdatum kein Kurs vorlag, aus dem sich ein Stückzahl schätzen ließe - trotzdem ein gültiger
/// Vorschlag, nur einer, der die Stückzahl von Hand braucht. <c>Confident</c> gilt für alle Buchungen
/// desselben Wertpapiers gemeinsam (siehe <see cref="SecuritiesMatchSummary"/>).
/// </summary>
public sealed record SecuritiesBookingMatch(
    Guid TransactionId,
    Guid SecurityId,
    DateOnly Date,
    decimal Gross,
    string Currency,
    decimal? Quantity,
    bool QuantityEstimated,
    bool Confident);

/// <summary>
/// Je Wertpapier eine Zusammenfassung über alle seine gefundenen Buchungen in diesem Lauf: der
/// aktuelle Bestand (Q, P) aus dem Depot, die Summe der gefundenen Buchungen, und ob diese Summe nah
/// genug am Bestand liegt, um automatisch übernommen zu werden. Nützlich für eine UI, die vor dem
/// Anwenden zeigen will, warum ein Wertpapier (nicht) automatisch übernommen wurde.
/// </summary>
public sealed record SecuritiesMatchSummary(
    Guid SecurityId,
    decimal? CurrentQuantity,
    decimal? CurrentCostPrice,
    decimal SumGross,
    decimal? SumQuantity,
    int BookingCount,
    bool Confident);

/// <summary>Ergebnis eines <see cref="SecuritiesBookingMatcher.Match"/>-Laufs.</summary>
public sealed record SecuritiesBookingMatchBatch(
    IReadOnlyList<SecuritiesBookingMatch> Matches,
    IReadOnlyList<SecuritiesMatchSummary> Summaries);

/// <summary>
/// Findet Wertpapierkäufe in Girokonto-Buchungen - rein, ohne Datenbank und ohne I/O, damit ein
/// späterer Slice ihn mit echten Daten aus Buchungen, Wertpapieren, Kursen und Depotbestand füttern
/// kann, ohne dass ein Test dafür Postgres braucht.
///
/// FinTS/MT535 liefert nur den heutigen Bestand, keine Kaufhistorie. Also wird der Kauf aus der
/// Buchung rekonstruiert, die ihn bezahlt hat - dem Beleg im Kontoauszug, nicht dem Depotauszug.
/// </summary>
public static partial class SecuritiesBookingMatcher
{
    /// <summary>
    /// Eigener Name statt einer nackten Zahl im Code: 1% ist eine bewusste Toleranzentscheidung
    /// (Rundungsdifferenzen zwischen geschätzter und tatsächlicher Stückzahl/Kurs), keine Konstante der
    /// Mathematik - wer sie ändert, soll eine Stelle finden, nicht mehrere.
    /// </summary>
    public const decimal ConfidenceTolerance = 0.01m;

    // ?NN zerlegt den :86:-Text in SWIFT-Unterfelder an ~27-Zeichen-Grenzen. Eine ISIN oder WKN, die
    // zufällig über eine solche Grenze fällt, ist im Rohtext in zwei Stücke zerrissen und muss vor der
    // Suche wieder zusammengefügt werden - siehe FinTsResponse.ParseMt940, das ?NN unangetastet in
    // Description ablegt.
    private static readonly Regex MarkerRegex = new(@"\?\d{2}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Wie BrokerPdfTradeParser.IsinRegex, aber mit einer eigenen Prüfzifferstelle statt "irgendein
    // zwölftes Zeichen" - der Broker-PDF-Parser braucht das nicht, weil dort die ISIN aus einer
    // beschrifteten Zeile kommt, hier steht sie frei im Fließtext eines Verwendungszwecks.
    private static readonly Regex IsinRegex = new(@"\b[A-Z]{2}[A-Z0-9]{9}[0-9]\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Sechs blanke alphanumerische Zeichen kommen in einem Verwendungszweck ständig zufällig vor
    // (Auftragsnummern, Referenzen). Nur zusammen mit dem Label "WKN" ist das Risiko einer falschen
    // Übereinstimmung klein genug.
    private static readonly Regex WknRegex = new(@"\bWKN\b\s*[:\-]?\s*(?<value>[A-Z0-9]{6})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Sucht in <paramref name="bookings"/> nach Wertpapierkäufen und schätzt je Treffer die Stückzahl
    /// aus <paramref name="pricesBySecurityId"/>. <paramref name="currentHoldingsBySecurityId"/> liefert
    /// je Wertpapier den heutigen Bestand (Q) und Einstandspreis (P) aus dem Depot, gegen den die Summe
    /// der gefundenen Buchungen geprüft wird, bevor sie als "sicher" gilt.
    /// </summary>
    public static SecuritiesBookingMatchBatch Match(
        IReadOnlyList<BookingCandidate> bookings,
        IReadOnlyList<SecurityCandidate> securities,
        IReadOnlyDictionary<Guid, IReadOnlyList<(DateOnly Date, decimal Price)>> pricesBySecurityId,
        IReadOnlyDictionary<Guid, (decimal Quantity, decimal? CostPrice)> currentHoldingsBySecurityId)
    {
        var isinIndex = BuildIndex(securities, s => s.Isin);
        var wknIndex = BuildIndex(securities, s => s.Wkn);

        var found = new List<(BookingCandidate Booking, Guid SecurityId, decimal? Quantity)>();
        foreach (var booking in bookings)
        {
            // Ignorierte und Umbuchungen sind nie ein Kauf - eine interne Umbuchung zwischen eigenen
            // Konten hat zufällig denselben Betrag wie eine echte Wertpapierabrechnung sein können, ist
            // aber keine.
            if (booking.IsIgnored || booking.IsTransfer) continue;
            // Betrag und Datum allein sind nie ein Anker (siehe Klassendokumentation) - erst hier, mit
            // einem gefundenen Wertpapier, wird aus einer Belastung ein Kauf-Vorschlag.
            if (booking.Amount >= 0m) continue;

            var securityId = FindSecurity(booking.Description, isinIndex, wknIndex);
            if (securityId is null) continue;

            var gross = Math.Abs(booking.Amount);
            var quantity = EstimateQuantity(gross, booking.Date, pricesBySecurityId.GetValueOrDefault(securityId.Value));
            found.Add((booking, securityId.Value, quantity));
        }

        var matches = new List<SecuritiesBookingMatch>(found.Count);
        var summaries = new List<SecuritiesMatchSummary>();

        foreach (var group in found.GroupBy(f => f.SecurityId))
        {
            var items = group.ToList();
            var hasHolding = currentHoldingsBySecurityId.TryGetValue(group.Key, out var holding);
            var q = hasHolding ? holding.Quantity : 0m;
            var p = hasHolding ? holding.CostPrice : null;

            var allHaveQuantity = items.All(i => i.Quantity.HasValue);
            var sumQuantity = allHaveQuantity ? items.Sum(i => i.Quantity!.Value) : (decimal?)null;
            var sumGross = items.Sum(i => Math.Abs(i.Booking.Amount));

            var confident = hasHolding && q > 0m && p is not null && allHaveQuantity && sumQuantity is not null
                && Math.Abs(sumQuantity.Value - q) <= ConfidenceTolerance * q
                && Math.Abs(sumGross - q * p.Value) <= ConfidenceTolerance * Math.Abs(q * p.Value);

            summaries.Add(new SecuritiesMatchSummary(
                group.Key, hasHolding ? q : null, p, sumGross, sumQuantity, items.Count, confident));

            if (confident)
            {
                // Die geschätzten Stückzahlen sind auf den Cent genau nur zufällig richtig - für die
                // Übernahme in ein "buy"-Trade zählt aber die exakte Summe Q, sonst driftet der
                // Depotbestand bei jeder weiteren automatischen Buchung ein Stück weiter auseinander.
                // Der Rest geht auf die größte Position, nicht verteilt, weil eine verteilte Rundung an
                // jeder Position ihrerseits neu erklärt werden müsste.
                var scale = q / sumQuantity!.Value;
                var scaled = items.Select(i => Math.Round(i.Quantity!.Value * scale, 8, MidpointRounding.AwayFromZero)).ToList();
                var remainder = q - scaled.Sum();
                var largestIndex = 0;
                for (var idx = 1; idx < scaled.Count; idx++)
                    if (scaled[idx] > scaled[largestIndex]) largestIndex = idx;
                scaled[largestIndex] += remainder;

                for (var idx = 0; idx < items.Count; idx++)
                {
                    var (booking, securityId, _) = items[idx];
                    matches.Add(new SecuritiesBookingMatch(booking.TransactionId, securityId, booking.Date,
                        Math.Abs(booking.Amount), booking.Currency, scaled[idx], true, true));
                }
            }
            else
            {
                foreach (var (booking, securityId, quantity) in items)
                {
                    matches.Add(new SecuritiesBookingMatch(booking.TransactionId, securityId, booking.Date,
                        Math.Abs(booking.Amount), booking.Currency, quantity, quantity.HasValue, false));
                }
            }
        }

        var ordered = matches.OrderBy(m => m.Date).ThenBy(m => m.TransactionId).ToList();
        return new SecuritiesBookingMatchBatch(ordered, summaries);
    }

    private static Dictionary<string, Guid> BuildIndex(IReadOnlyList<SecurityCandidate> securities, Func<SecurityCandidate, string?> selector)
    {
        var index = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var security in securities)
        {
            var key = selector(security);
            if (string.IsNullOrWhiteSpace(key)) continue;
            var normalized = key.Trim().ToUpperInvariant();
            // Die erste Zuordnung gewinnt; zwei Wertpapiere mit derselben ISIN/WKN im selben Bestand
            // wären ohnehin ein Datenfehler, den dieser Matcher nicht auflösen kann.
            index.TryAdd(normalized, security.SecurityId);
        }
        return index;
    }

    private static Guid? FindSecurity(string? description, Dictionary<string, Guid> isinIndex, Dictionary<string, Guid> wknIndex)
    {
        var cleaned = CleanText(description);
        if (cleaned.Length == 0) return null;

        var upper = cleaned.ToUpperInvariant();
        foreach (Match candidate in IsinRegex.Matches(upper))
        {
            if (!IsValidIsinCheckDigit(candidate.Value)) continue;
            if (isinIndex.TryGetValue(candidate.Value, out var securityId)) return securityId;
        }

        var wknMatch = WknRegex.Match(cleaned);
        if (wknMatch.Success)
        {
            var wkn = wknMatch.Groups["value"].Value.ToUpperInvariant();
            if (wknIndex.TryGetValue(wkn, out var securityId)) return securityId;
        }

        return null;
    }

    /// <summary>
    /// ?NN-Marken entfernen und Whitespace zusammenziehen, bevor überhaupt nach einer ISIN/WKN gesucht
    /// wird - sonst reißt eine SWIFT-Feldgrenze mitten durch ein Wertpapierkennzeichen, und die Suche
    /// findet nie etwas, obwohl der Text die Kennung vollständig enthält.
    /// </summary>
    private static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var withoutMarkers = MarkerRegex.Replace(text, "");
        return WhitespaceRegex.Replace(withoutMarkers, " ").Trim();
    }

    /// <summary>
    /// Nächster Kurs am oder vor dem Buchungsdatum, daraus die Stückzahl. Ohne Kurs bleibt die Buchung
    /// ein gültiger Treffer - nur ohne Stückzahl, die dann von Hand nachgetragen werden muss.
    /// </summary>
    private static decimal? EstimateQuantity(decimal gross, DateOnly date, IReadOnlyList<(DateOnly Date, decimal Price)>? prices)
    {
        if (prices is null || prices.Count == 0) return null;

        decimal? bestPrice = null;
        DateOnly? bestDate = null;
        foreach (var (priceDate, price) in prices)
        {
            if (priceDate > date || price <= 0m) continue;
            if (bestDate is null || priceDate > bestDate) { bestDate = priceDate; bestPrice = price; }
        }

        return bestPrice is null ? null : gross / bestPrice.Value;
    }

    /// <summary>
    /// Eigene Mod-10/Luhn-Prüfung der ISIN-Prüfziffer. <c>PensionIdentity.IsValidIsin</c> (Modules/Pension)
    /// prüft absichtlich nur die Syntax, nicht die Prüfziffer, und wäre hier ohnehin modulfremd
    /// (Reconciliation darf nicht nach Pension greifen). Jeder Buchstabe wird auf seine Alphabetposition
    /// erweitert (A=10 .. Z=35), danach ist es derselbe Luhn-Algorithmus wie bei einer Kreditkartennummer:
    /// von rechts gezählt bleibt die Prüfziffer selbst einfach, jede zweite Ziffer davor wird verdoppelt.
    /// </summary>
    internal static bool IsValidIsinCheckDigit(string isin)
    {
        if (isin.Length != 12) return false;

        var expanded = new StringBuilder(24);
        foreach (var ch in isin)
        {
            if (char.IsAsciiDigit(ch)) expanded.Append(ch);
            else if (char.IsAsciiLetterUpper(ch)) expanded.Append((ch - 'A' + 10).ToString(CultureInfo.InvariantCulture));
            else return false;
        }

        var digits = expanded.ToString();
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var posFromRight = digits.Length - 1 - i;
            var d = digits[i] - '0';
            if (posFromRight % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }
}
