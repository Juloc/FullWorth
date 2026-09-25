using FullWorth.Backend.Documents;
namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Was in der Datei steht, bevor der Nutzer sagen muss, woher sie kommt (#131, Schritt 2).
///
/// <c>Adapter</c> ist der Weg, der diese Datei verarbeiten kann. <c>ReasonKey</c> sagt WORAN das
/// erkannt wurde - als Schluessel, nicht als Satz: die Oberflaeche schreibt den Satz, hier steht die
/// Tatsache. <c>Accounts</c>, <c>Rows</c>, <c>From</c>/<c>To</c> sind das, was sich ohne Zutun schon
/// sagen laesst; was eine Quelle nicht hergibt, bleibt leer statt geraten zu werden.
/// </summary>
public sealed record ImportSourceDetection(
    string Adapter,
    string Confidence,
    string ReasonKey,
    string FileName,
    int Rows,
    IReadOnlyList<string> Accounts,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<string> Headers,
    ImportColumnMapping? SuggestedMapping);

/// <summary>
/// Erkennt die Importquelle am INHALT, nicht an der Endung - dieselbe Entscheidung, die
/// <see cref="BankStatementFile"/> fuer seine zwei Formate schon trifft, nur eine Ebene hoeher.
///
/// Vorher musste der Nutzer zuerst eine von fuenf Kacheln waehlen und danach die Datei. Wer falsch
/// waehlte, landete in einer Sackgasse, deren Fehlermeldung von der Datei handelte und nicht von der
/// Wahl. Die Datei weiss selbst, was sie ist.
///
/// Geraten wird hier nichts: jede Antwort nennt ihren Grund, und was sich nicht belegen laesst, heisst
/// <c>unknown</c> - dann waehlt weiterhin der Nutzer.
/// </summary>
public static class ImportSourceDetector
{
    public const string AdapterFinanzguru = "finanzguru";
    public const string AdapterStatement = "statement";
    public const string AdapterInvestments = "investments";
    public const string AdapterTransactions = "transactions";
    public const string AdapterBrokerPdf = "broker-pdf";
    public const string AdapterUnknown = "unknown";

    public const string Certain = "certain";
    public const string Likely = "likely";
    public const string Unknown = "unknown";

    /// <summary>
    /// Spalten, die es nur in einem Depotexport gibt. Eine davon reicht: ein Buchungsexport fuehrt
    /// weder ISIN noch WKN noch Stueckzahl.
    /// </summary>
    private static readonly string[] InvestmentColumns =
        ["isin", "wkn", "stueck", "stueckzahl", "shares", "anteile", "quantity", "assetclass", "wertpapier", "ticker"];

    /// <param name="pdfPages">
    /// Die Zeilen eines PDF, wenn es eines ist - vom Aufrufer gelesen, weil das ein Werkzeug startet und
    /// diese Erkennung selbst nichts ausfuehrt. <c>null</c> heisst: kein PDF, oder nicht lesbar.
    /// </param>
    public static ImportSourceDetection Detect(
        string fileName, byte[] bytes, IReadOnlyList<IReadOnlyList<PdfLine>>? pdfPages = null)
    {
        var name = Path.GetFileName(fileName);

        // Ein PDF ist an seinen ersten fuenf Bytes zu erkennen. Welches PDF es ist, sagt sein Inhalt: ein
        // Kontoauszug, den der Statement-Leser kennt (#131, Abschnitt 11), geht dorthin; alles andere
        // bleibt beim Depot-Weg, der Broker-Abrechnungen liest.
        if (BankStatementPdf.IsPdf(bytes))
        {
            if (pdfPages is not null && BankStatementPdf.Recognises(pdfPages))
            {
                try { return FromStatement(BankStatementPdf.Read(pdfPages), name); }
                catch (Exception exception) when (exception is InvalidDataException or FormatException) { }
            }
            return Empty(AdapterBrokerPdf, Likely, "pdf", name);
        }

        // Ein Finanzguru-Export ist eine Arbeitsmappe mit vierzehn festen Kopfzeilen. Trifft das zu, ist
        // die Antwort sicher - und die Datei sagt dann auch gleich, welche Konten und welcher Zeitraum
        // drinstehen, ohne dass irgendetwas geschrieben werden muesste.
        if (bytes.Length >= 2 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            var finanzguru = TryReadFinanzguru(bytes);
            if (finanzguru is not null)
            {
                var accounts = finanzguru
                    .Select(row => (row.ReferenceAccountName ?? row.ReferenceAccount)?.Trim())
                    .Where(account => !string.IsNullOrWhiteSpace(account))
                    .Select(account => account!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(account => account, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                return new ImportSourceDetection(
                    AdapterFinanzguru, Certain, "finanzguruHeaders", name, finanzguru.Count, accounts,
                    finanzguru.Count == 0 ? null : finanzguru.Min(row => row.BookingDate),
                    finanzguru.Count == 0 ? null : finanzguru.Max(row => row.BookingDate),
                    [], null);
            }
        }

        // MT940 und CAMT erkennt der Statement-Leser an seinem eigenen Inhalt. Er wirft, wenn es keines
        // von beiden ist - genau das ist hier die Antwort "nein", nicht ein Fehler.
        var statement = TryReadStatement(bytes);
        if (statement is not null) return FromStatement(statement, name);

        if (!ImportTabularFile.CouldBeTable(Path.GetExtension(name)))
            return Empty(AdapterUnknown, Unknown, "unreadable", name);

        List<Dictionary<string, string>> rows;
        try { rows = ImportTabularFile.Read(name, bytes); }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        { return Empty(AdapterUnknown, Unknown, "unreadable", name); }
        if (rows.Count == 0) return Empty(AdapterUnknown, Unknown, "noRows", name);

        var headers = rows[0].Keys.ToArray();
        var mapping = ImportTabularFile.SuggestColumns(headers);
        if (headers.Any(header => InvestmentColumns.Contains(Norm(header))))
            return new ImportSourceDetection(AdapterInvestments, Likely, "investmentColumns", name, rows.Count, [], null, null, headers, mapping);

        // Datum und Betrag sind das Minimum, aus dem eine Buchung wird. Fehlt eines, ist die Tabelle
        // zwar lesbar, aber wofuer sie gut ist, weiss diese Datei nicht - dann waehlt der Nutzer.
        if (mapping.Date.Length > 0 && mapping.Amount.Length > 0)
            return new ImportSourceDetection(AdapterTransactions, Likely, "tabularColumns", name, rows.Count, [], null, null, headers, mapping);

        return new ImportSourceDetection(AdapterUnknown, Unknown, "unmappedColumns", name, rows.Count, [], null, null, headers, mapping);
    }

    private static ImportSourceDetection FromStatement(BankStatement statement, string name)
    {
        var dates = statement.Entries.Select(entry => entry.BookingDate).ToArray();
        return new ImportSourceDetection(
            AdapterStatement, Certain, statement.AdapterKey, name, statement.Entries.Count,
            statement.AccountIdentifier is null ? [] : [statement.AccountIdentifier],
            dates.Length == 0 ? statement.ClosingBalance?.AsOf : dates.Min(),
            dates.Length == 0 ? statement.ClosingBalance?.AsOf : dates.Max(),
            [], null);
    }

    private static ImportSourceDetection Empty(string adapter, string confidence, string reasonKey, string fileName) =>
        new(adapter, confidence, reasonKey, fileName, 0, [], null, null, [], null);

    private static string Norm(string value) => new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static IReadOnlyList<FinanzguruRow>? TryReadFinanzguru(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new FinanzguruWorkbookReader().Read(stream);
        }
        catch (FinanzguruWorkbookException) { return null; }
        catch (Exception exception) when (exception is InvalidDataException or FormatException) { return null; }
    }

    private static BankStatement? TryReadStatement(byte[] bytes)
    {
        try { return BankStatementFile.Read(bytes); }
        catch (Exception exception) when (exception is InvalidDataException or FormatException) { return null; }
    }
}
