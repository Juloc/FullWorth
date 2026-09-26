using System.Globalization;
using System.Text.RegularExpressions;
using FullWorth.Backend.Documents;
using FullWorth.Backend.Validation;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Kontoauszuege als PDF (#131, Abschnitt 11) - fuer Konten, deren Bank weder eine Anbindung noch
/// MT940/CAMT anbietet. Das Ergebnis ist derselbe <see cref="BankStatement"/> wie aus MT940/CAMT; alles
/// danach - Vorschau, Dubletten, Festschreiben, Kontostand, Ruecknahme - ist der Weg, den es schon gibt.
///
/// Der Grundsatz, der hier mehr zaehlt als irgendwo sonst im Import: <b>ein PDF wird nachgerechnet,
/// nicht geglaubt.</b> Ein Auszug nennt seinen Anfangs- und Endstand. Geht die Summe der gelesenen
/// Buchungen nicht exakt vom einen zum anderen, ist mindestens ein Betrag oder Vorzeichen falsch gelesen
/// - dann wird keine Zeile still uebernommen, sondern jede zur Pruefung vorgelegt.
///
/// Warnungen und Pruefnotizen sind Schluessel, keine Saetze: die Oberflaeche spricht zwei Sprachen.
/// </summary>
internal static partial class BankStatementPdf
{
    internal const string IkanoAdapter = "ikano_pdf";
    internal const string C24Adapter = "c24_pdf";

    /// <summary>Die gelesenen Buchungen fuehren nicht vom Anfangs- zum Endstand.</summary>
    internal const string NotReconciled = "not_reconciled";

    /// <summary>
    /// Eine Umbuchung innerhalb desselben Kontos - bei Ikano zwischen Kreditkarte und
    /// Ratenkauf-Finanzierung. Sie hebt sich im Konto auf und aendert seinen Stand nicht.
    /// </summary>
    internal const string InternalTransfer = "internal_transfer";

    /// <summary>Der Auszug hat Buchungen, deren Zeilenformat FullWorth noch nicht liest.</summary>
    internal const string RowsNotRead = "rows_not_read";

    /// <summary>Kreditrahmen minus verfuegbarer Betrag ergibt nicht den genannten Saldo.</summary>
    internal const string CreditLimitMismatch = "credit_limit_mismatch";

    internal static bool IsPdf(byte[] bytes) =>
        bytes.Length >= 5 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' && bytes[4] == '-';

    /// <summary>Waehlt den Leser am Inhalt. Ein PDF, das keiner kennt, ist ein Fehler, keine leere Datei.</summary>
    internal static BankStatement Read(IReadOnlyList<IReadOnlyList<PdfLine>> pages)
    {
        var lines = pages.SelectMany(page => page).ToList();
        var text = string.Join('\n', lines.Select(line => line.Text));
        if (Ikano.Matches(text)) return Ikano.Read(lines, text);
        if (C24.Matches(text)) return C24.Read(pages, text);
        throw new InvalidDataException(
            "FullWorth cannot read this PDF statement yet. Supported are Ikano and C24 statements.");
    }

    /// <summary>Ob <see cref="Read"/> dieses PDF als Kontoauszug erkennt - fuer die Quellenerkennung.</summary>
    internal static bool Recognises(IReadOnlyList<IReadOnlyList<PdfLine>> pages)
    {
        var text = string.Join('\n', pages.SelectMany(page => page).Select(line => line.Text));
        return Ikano.Matches(text) || C24.Matches(text);
    }

    private static decimal Amount(string text) => ImportNumber.Parse(text, ImportNumber.ThreeDigitTail.Grouping);

    /// <summary>"123,45 -" ist bei Ikano eine Belastung, "123,45 +" eine Gutschrift.</summary>
    private static decimal Signed(string amount, string? sign) => sign == "-" ? -Amount(amount) : Amount(amount);

    private static DateOnly Date(string text) =>
        DateOnly.ParseExact(text, text.Length == 8 ? "dd.MM.yy" : "dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// IKEA-Kreditkarte der Ikano Bank: ein Konto mit zwei Abschnitten - die Kartenumsaetze und die
    /// Ratenkauf-Finanzierung. Zwischen beiden schiebt Ikano Betraege per "Automatische
    /// Saldenkorrektur" und "Umbuchung in Ratenkauf" hin und her; deshalb geht jeder Abschnitt fuer sich
    /// nicht auf, der Auszug als Ganzes aber auf den Cent.
    /// </summary>
    internal static partial class Ikano
    {
        internal static bool Matches(string text) =>
            text.Contains("Ikano Bank", StringComparison.Ordinal) && text.Contains("Abrechnung vom", StringComparison.Ordinal);

        internal static BankStatement Read(IReadOnlyList<PdfLine> lines, string text)
        {
            var warnings = new List<string>();
            var asOf = DatePattern().Match(text) is { Success: true } dated
                ? Date(dated.Groups["date"].Value)
                : PeriodPattern().Match(text) is { Success: true } period
                    ? Date(period.Groups["to"].Value)
                    : throw new InvalidDataException("The Ikano statement states no date.");

            // Die Kontokennung ist die Kartennummer, NICHT die IBAN im Kopf: dort steht das Girokonto,
            // von dem die Rate abgebucht wird. Mit ihr landete der Auszug auf dem falschen Konto.
            var identifier = CardPattern().Match(text) is { Success: true } card
                ? $"Ikano •••• {card.Groups["last4"].Value}"
                : ContractPattern().Match(text) is { Success: true } contract
                    ? $"Ikano {contract.Groups["id"].Value}"
                    : null;

            var entries = new List<StatementEntry>();
            foreach (var line in lines)
            {
                var row = RowPattern().Match(line.Text);
                // Eine Zeile ohne Vorzeichen ist keine Buchung, sondern der Ratenplan ("RATE ... 40,00"):
                // die drei ergeben zusammen die "Monatsrate Finanzierung". Die Nachrechnung unten haelt
                // das fest - waere eine davon doch eine Buchung, ginge der Auszug nicht auf.
                if (!row.Success || !row.Groups["sign"].Success) continue;

                var receipt = Date(row.Groups["receipt"].Value);
                var booked = row.Groups["booked"].Success ? Date(row.Groups["booked"].Value) : receipt;
                var details = row.Groups["details"].Value.Trim();
                entries.Add(new StatementEntry(
                    booked, receipt, Signed(row.Groups["amount"].Value, row.Groups["sign"].Value), "EUR",
                    details, null, null));
            }

            var opening = OpeningPattern().Match(text);
            var closing = ClosingPattern().Match(text);
            decimal? openingAmount = opening.Success ? Signed(opening.Groups["amount"].Value, opening.Groups["sign"].Value) : null;
            decimal? closingAmount = closing.Success ? Signed(closing.Groups["amount"].Value, closing.Groups["sign"].Value) : null;

            var reconciled = openingAmount is not null && closingAmount is not null
                && openingAmount.Value + entries.Sum(entry => entry.Amount) == closingAmount.Value;

            if (!reconciled)
            {
                warnings.Add(NotReconciled);
                entries = [.. entries.Select(entry => entry with { ReviewNote = NotReconciled })];
            }
            else
            {
                // Die Umbuchungen zwischen Karte und Finanzierung heben sich im Konto auf. Sie werden
                // gezeigt, aber nicht vorgewaehlt - sonst stuenden zu vier echten Buchungen zehn, die
                // zusammen null ergeben. Nur wenn sie sich wirklich aufheben: sonst tragen sie Wert und
                // bleiben normale Zeilen.
                var internalRows = entries.Where(entry => IsInternal(entry.Counterparty)).ToList();
                if (internalRows.Count > 0 && internalRows.Sum(entry => entry.Amount) == 0m)
                    entries = [.. entries.Select(entry => IsInternal(entry.Counterparty) ? entry with { ReviewNote = InternalTransfer } : entry)];
            }

            // Eine zweite, unabhaengige Probe des Saldos: Kreditrahmen minus verfuegbarer Betrag.
            // null heisst: der Auszug nennt eines von beiden nicht, die Probe entfaellt.
            bool? limitConfirms = closingAmount is not null
                && LimitPattern().Match(text) is { Success: true } limit
                && AvailablePattern().Match(text) is { Success: true } available
                    ? Amount(limit.Groups["amount"].Value) - Amount(available.Groups["amount"].Value) == -closingAmount.Value
                    : null;
            if (limitConfirms == false) warnings.Add(CreditLimitMismatch);

            // Der Saldo gilt, wenn mindestens eine der zwei Proben ihn traegt: die Buchungen fuehren zu
            // ihm, oder der Kreditrahmen bestaetigt ihn. Scheitert beides, ist er so wenig gesichert wie
            // die Zeilen - und ein falscher Kontostand ist schlimmer als keiner, genau wie bei C24.
            var balanceHolds = closingAmount is not null && (reconciled || limitConfirms == true);

            return new BankStatement(
                IkanoAdapter,
                entries,
                balanceHolds ? new StatementBalance(closingAmount!.Value, "EUR", asOf) : null,
                identifier,
                warnings);
        }

        private static bool IsInternal(string? details) =>
            details is not null
            && (details.StartsWith("Automatische Saldenkorrektur", StringComparison.OrdinalIgnoreCase)
                || details.StartsWith("UMBUCHUNG IN RATENKAUF", StringComparison.OrdinalIgnoreCase)
                || details.StartsWith("RATENKAUF,", StringComparison.OrdinalIgnoreCase));

        [GeneratedRegex(@"Abrechnung vom (?<from>\d{2}\.\d{2}\.\d{4}) bis (?<to>\d{2}\.\d{2}\.\d{4})")]
        private static partial Regex PeriodPattern();

        [GeneratedRegex(@"Datum:\s*(?<date>\d{2}\.\d{2}\.\d{4})")]
        private static partial Regex DatePattern();

        [GeneratedRegex(@"Kreditkartennummer:\s*[\dX]{4} [\dX]{4} [\dX]{4} (?<last4>\d{4})")]
        private static partial Regex CardPattern();

        [GeneratedRegex(@"Vertrags-ID:\s*(?<id>\d+)")]
        private static partial Regex ContractPattern();

        [GeneratedRegex(@"Gesamtsaldo alt:\s*(?<amount>\d{1,3}(?:\.\d{3})*,\d{2})\s*(?<sign>[+-])?")]
        private static partial Regex OpeningPattern();

        [GeneratedRegex(@"Saldo gesamt:\s*(?<amount>\d{1,3}(?:\.\d{3})*,\d{2})\s*(?<sign>[+-])?")]
        private static partial Regex ClosingPattern();

        [GeneratedRegex(@"Kreditrahmen:\s*(?<amount>\d{1,3}(?:\.\d{3})*(?:,\d{2})?)\s*EUR")]
        private static partial Regex LimitPattern();

        [GeneratedRegex(@"Verfügbarer Betrag:\s*(?<amount>\d{1,3}(?:\.\d{3})*,\d{2})\s*EUR")]
        private static partial Regex AvailablePattern();

        /// <summary>Belegdatum, optional Buchungsdatum, Buchungstext, Betrag, optional Vorzeichen.</summary>
        [GeneratedRegex(@"^(?<receipt>\d{2}\.\d{2}\.\d{2})\s+(?:(?<booked>\d{2}\.\d{2}\.\d{2})\s+)?(?<details>.+?)\s+(?<amount>\d{1,3}(?:\.\d{3})*,\d{2})(?:\s*(?<sign>[+-]))?$")]
        private static partial Regex RowPattern();
    }

    /// <summary>
    /// C24 Smartkonto. Die Transaktionsuebersicht hat je Buchung eine Zeile "Buchung Valuta Art Betrag"
    /// - die Daten ohne Jahr, das steht nur im Zeitraum - und darunter ihre Einzelheiten: bei einer
    /// Kartenzahlung der Haendler, bei einer Ueberweisung Name, Verwendungszweck und IBAN. Die
    /// Zusammenfassung nennt Start- und Endsaldo, Belastungen und Gutschriften; alle vier werden gegen
    /// die gelesenen Zeilen nachgerechnet.
    /// </summary>
    internal static partial class C24
    {
        internal static bool Matches(string text) =>
            text.Contains("C24 Bank", StringComparison.Ordinal) && text.Contains("Kontoauszug", StringComparison.Ordinal);

        internal static BankStatement Read(IReadOnlyList<IReadOnlyList<PdfLine>> pages, string text)
        {
            var warnings = new List<string>();
            var period = PeriodPattern().Match(text);
            if (!period.Success) throw new InvalidDataException("The C24 statement states no period.");
            var from = Date(period.Groups["from"].Value);
            var asOf = Date(period.Groups["to"].Value);
            var identifier = IbanPattern().Match(text) is { Success: true } iban ? iban.Groups["iban"].Value : null;
            var entries = Rows(pages, from, asOf);

            decimal? Summary(Regex pattern) =>
                pattern.Match(text) is { Success: true } match ? Amount(match.Groups["amount"].Value) : null;
            var start = Summary(StartPattern());
            var debits = Summary(DebitsPattern());
            var credits = Summary(CreditsPattern());
            var end = Summary(EndPattern());
            var stated = Summary(BalancePattern());

            // Startsaldo + Gutschriften + Belastungen (die ihr Minus mitbringen) = Endsaldo, und der
            // Endsaldo ist der Kontostand im Kopf. Beides muss stimmen, sonst ist eine der Zahlen falsch
            // gelesen - und ein falscher Kontostand ist schlimmer als keiner.
            var reconciled = start is not null && debits is not null && credits is not null && end is not null
                && start.Value + credits.Value + debits.Value == end.Value
                && (stated is null || stated.Value == end.Value);
            // Die Zeilen gegen die Zusammenfassung: zusammen vom Start- zum Endsaldo, und Belastungen wie
            // Gutschriften je fuer sich - ein vertauschtes Vorzeichen gleicht sich in der Summe sonst aus.
            var rowsReconcile = reconciled
                && entries.Sum(entry => entry.Amount) == end!.Value - start!.Value
                && entries.Where(entry => entry.Amount > 0).Sum(entry => entry.Amount) == credits!.Value
                && entries.Where(entry => entry.Amount < 0).Sum(entry => entry.Amount) == debits!.Value;

            var noRows = text.Contains("Keine Transaktionen im Zeitraum vorhanden", StringComparison.Ordinal);
            if (entries.Count == 0 && !noRows)
            {
                // Der Auszug hat Buchungen, aber keine Zeile hat das bekannte Format.
                warnings.Add(RowsNotRead);
                if (!reconciled) warnings.Add(NotReconciled);
            }
            else if (!rowsReconcile)
            {
                warnings.Add(NotReconciled);
                entries = [.. entries.Select(entry => entry with { ReviewNote = NotReconciled })];
            }

            // Der Kontostand haengt an der Zusammenfassung und dem Kopf, nicht an den Zeilen: das sind
            // zwei unabhaengige Angaben des Auszugs, die uebereinstimmen muessen.
            return new BankStatement(
                C24Adapter,
                entries,
                reconciled ? new StatementBalance(end!.Value, "EUR", asOf) : null,
                identifier,
                warnings);
        }

        /// <summary>
        /// Die Buchungen der Transaktionsuebersicht. Je Seite beginnt die Tabelle nach ihrer Kopfzeile und
        /// endet an der Zusammenfassung oder am Seitenfuss - so geraten weder Anschrift noch Rechtstext
        /// einer Folgeseite in die Einzelheiten einer Buchung.
        /// </summary>
        private static List<StatementEntry> Rows(IReadOnlyList<IReadOnlyList<PdfLine>> pages, DateOnly from, DateOnly to)
        {
            var entries = new List<StatementEntry>();
            foreach (var page in pages)
            {
                var inTable = false;
                (Match Row, List<string> Details)? open = null;
                foreach (var line in page)
                {
                    var content = line.Text.Trim();
                    if (TableHeaderPattern().IsMatch(content)) { inTable = true; continue; }
                    if (!inTable) continue;
                    if (content.StartsWith("Zusammenfassung", StringComparison.Ordinal) || FooterPattern().IsMatch(content))
                    {
                        inTable = false;
                        continue;
                    }
                    var row = RowPattern().Match(content);
                    if (row.Success)
                    {
                        if (open is { } done) entries.Add(Entry(done.Row, done.Details, from, to));
                        open = (row, []);
                    }
                    else if (open is { } current && content.Length > 0)
                    {
                        current.Details.Add(content);
                    }
                }
                if (open is { } last) entries.Add(Entry(last.Row, last.Details, from, to));
            }
            return entries;
        }

        /// <summary>
        /// Die erste Zeile der Einzelheiten ist die Gegenseite - Haendler oder Name -, der Rest mit der Art
        /// der Buchung davor die Beschreibung. Ohne Einzelheiten ist die Art selbst die Gegenseite.
        /// </summary>
        private static StatementEntry Entry(Match row, List<string> details, DateOnly from, DateOnly to)
        {
            var kind = row.Groups["kind"].Value.Trim();
            var counterparty = details.Count > 0 ? details[0] : kind;
            var description = string.Join(" · ", new[] { kind }.Concat(details.Skip(1)));
            return new StatementEntry(
                DayOf(row.Groups["booked"].Value, from, to),
                DayOf(row.Groups["value"].Value, from, to),
                Amount(row.Groups["amount"].Value),
                "EUR",
                counterparty,
                description,
                null);
        }

        /// <summary>
        /// "18.08." im Zeitraum eines Auszugs: das Jahr des Zeitraums. Reicht er ueber einen Jahreswechsel
        /// (Dezember bis Januar), gehoert ein Monat vor dem Beginn ins zweite Jahr.
        /// </summary>
        private static DateOnly DayOf(string text, DateOnly from, DateOnly to)
        {
            if (text.Length == 10) return Date(text);
            var day = int.Parse(text[..2], CultureInfo.InvariantCulture);
            var month = int.Parse(text.Substring(3, 2), CultureInfo.InvariantCulture);
            var year = from.Year == to.Year || month >= from.Month ? from.Year : to.Year;
            return new DateOnly(year, month, day);
        }

        [GeneratedRegex(@"^Buchung\s+Valuta\s+Transaktionsinformation\s+Betrag$")]
        private static partial Regex TableHeaderPattern();

        [GeneratedRegex(@"C24 Bank GmbH|Seite \d+ von \d+")]
        private static partial Regex FooterPattern();

        /// <summary>Buchungstag, Valuta (je ohne oder mit Jahr), Art der Buchung, Betrag mit Vorzeichen.</summary>
        [GeneratedRegex(@"^(?<booked>\d{2}\.\d{2}\.(?:\d{4})?)\s+(?<value>\d{2}\.\d{2}\.(?:\d{4})?)\s+(?<kind>.+?)\s+(?<amount>[+-]\d{1,3}(?:\.\d{3})*,\d{2})\s*€$")]
        private static partial Regex RowPattern();

        [GeneratedRegex(@"(?<from>\d{2}\.\d{2}\.\d{4})\s*-\s*(?<to>\d{2}\.\d{2}\.\d{4})")]
        private static partial Regex PeriodPattern();

        [GeneratedRegex(@"IBAN:\s*(?<iban>DE\d{20})")]
        private static partial Regex IbanPattern();

        private const string SignedAmount = @"(?<amount>[+-]?\d{1,3}(?:\.\d{3})*,\d{2})";

        [GeneratedRegex(@"Startsaldo\s+" + SignedAmount)]
        private static partial Regex StartPattern();

        [GeneratedRegex(@"Kontobelastungen\s+" + SignedAmount)]
        private static partial Regex DebitsPattern();

        [GeneratedRegex(@"Kontogutschriften\s+" + SignedAmount)]
        private static partial Regex CreditsPattern();

        [GeneratedRegex(@"Endsaldo\s+" + SignedAmount)]
        private static partial Regex EndPattern();

        [GeneratedRegex(@"Kontostand\s+" + SignedAmount)]
        private static partial Regex BalancePattern();
    }
}
