using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FullWorth.Backend.Modules.Import;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// #131, Schritt 2: die Datei sagt selbst, woher sie kommt.
///
/// Vorher waehlte der Nutzer erst eine von fuenf Quellen und danach die Datei. Wer falsch waehlte,
/// landete in einer Sackgasse, deren Fehlermeldung von der Datei handelte und nicht von der Wahl.
///
/// Diese Tests halten beides fest: dass jede Quelle an ihrem INHALT erkannt wird (nicht an der
/// Endung - Banken benennen MT940 als .txt, .sta, .940 und .mt940 durcheinander), und dass eine Datei,
/// die sich nicht belegen laesst, <c>unknown</c> heisst statt geraten zu werden.
/// </summary>
public sealed class ImportSourceDetectionTests
{
    private const string Mt940 = """
        :20:STARTUMS
        :25:DE02120300000000202051/EUR
        :28C:00012/001
        :60F:C260901EUR1000,00
        :61:2609020902D42,19NMSCNONREF//BREF-1
        :86:?00KARTENZAHLUNG?20Einkauf Wochenmarkt?32REWE Markt GmbH
        :62F:C260905EUR957,81
        -
        """;

    private const string Camt = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
          <BkToCstmrStmt>
            <Stmt>
              <Id>STMT-1</Id>
              <Acct><Id><IBAN>DE02120300000000202051</IBAN></Id><Ccy>EUR</Ccy></Acct>
              <Bal>
                <Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp>
                <Amt Ccy="EUR">957.81</Amt><CdtDbtInd>CRDT</CdtDbtInd><Dt><Dt>2026-09-05</Dt></Dt>
              </Bal>
              <Ntry>
                <NtryRef>BREF-1</NtryRef>
                <Amt Ccy="EUR">42.19</Amt><CdtDbtInd>DBIT</CdtDbtInd>
                <BookgDt><Dt>2026-09-02</Dt></BookgDt>
                <NtryDtls><TxDtls><RmtInf><Ustrd>Einkauf</Ustrd></RmtInf></TxDtls></NtryDtls>
              </Ntry>
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;

    [Fact]
    public void A_finanzguru_workbook_is_recognised_by_its_headers()
    {
        var detection = ImportSourceDetector.Detect("export.xlsx", FinanzguruWorkbook(
            ("01.03.2026", "-12,34", "C24 Girokonto", "fg-1"),
            ("05.09.2026", "-9,99", "PayPal", "fg-2")));

        Assert.Equal(ImportSourceDetector.AdapterFinanzguru, detection.Adapter);
        Assert.Equal(ImportSourceDetector.Certain, detection.Confidence);
        Assert.Equal("finanzguruHeaders", detection.ReasonKey);
        Assert.Equal(2, detection.Rows);
        // Genau das, was der Nutzer im naechsten Schritt zuordnen muss - ohne dass etwas geschrieben wurde.
        Assert.Equal(["C24 Girokonto", "PayPal"], detection.Accounts);
        Assert.Equal(new DateOnly(2026, 3, 1), detection.From);
        Assert.Equal(new DateOnly(2026, 9, 5), detection.To);
    }

    /// <summary>
    /// Eine Arbeitsmappe, die nur ZUFAELLIG .xlsx heisst, darf nicht als Finanzguru durchgehen - der
    /// Finanzguru-Weg importiert ohne Spaltenzuordnung und wuerde eine fremde Tabelle falsch lesen.
    /// </summary>
    [Fact]
    public void A_workbook_without_the_finanzguru_headers_falls_through_to_the_generic_path()
    {
        var detection = ImportSourceDetector.Detect("bank.xlsx", Workbook(
            ["Datum", "Betrag", "Empfänger"],
            ["01.03.2026", "-12,34", "REWE"]));

        Assert.Equal(ImportSourceDetector.AdapterTransactions, detection.Adapter);
        Assert.Equal("tabularColumns", detection.ReasonKey);
        Assert.Equal(1, detection.Rows);
    }

    [Theory]
    [InlineData("auszug.txt")]
    [InlineData("auszug.sta")]
    [InlineData("kontoauszug.mt940")]
    public void An_mt940_statement_is_recognised_whatever_the_bank_called_the_file(string fileName)
    {
        var detection = ImportSourceDetector.Detect(fileName, Encoding.UTF8.GetBytes(Mt940));

        Assert.Equal(ImportSourceDetector.AdapterStatement, detection.Adapter);
        Assert.Equal(ImportSourceDetector.Certain, detection.Confidence);
        Assert.Equal("mt940", detection.ReasonKey);
        Assert.Equal(1, detection.Rows);
        // So, wie die Datei es nennt - nicht aufgehuebscht: der Nutzer soll vergleichen koennen.
        Assert.Equal(["DE02120300000000202051/EUR"], detection.Accounts);
    }

    [Fact]
    public void A_camt_statement_is_recognised_and_names_its_account()
    {
        var detection = ImportSourceDetector.Detect("statement.xml", Encoding.UTF8.GetBytes(Camt));

        Assert.Equal(ImportSourceDetector.AdapterStatement, detection.Adapter);
        Assert.Equal("camt", detection.ReasonKey);
        Assert.Equal(["DE02120300000000202051"], detection.Accounts);
    }

    [Fact]
    public void A_pdf_is_recognised_by_its_first_bytes()
    {
        var detection = ImportSourceDetector.Detect("abrechnung.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n%âãÏÓ\n"));

        Assert.Equal(ImportSourceDetector.AdapterBrokerPdf, detection.Adapter);
        Assert.Equal("pdf", detection.ReasonKey);
    }

    /// <summary>
    /// Ein Depotexport fuehrt Spalten, die ein Buchungsexport nie hat. Eine davon reicht - ohne diese
    /// Unterscheidung waere jede Depotdatei als Buchungsdatei durchgegangen und haette Stueckzahlen als
    /// Betraege gebucht.
    /// </summary>
    [Theory]
    [InlineData("Datum;Typ;ISIN;Stück;Betrag")]
    [InlineData("date;type;wkn;shares;amount")]
    [InlineData("Datum;Art;Wertpapier;Anteile;Betrag")]
    public void A_portfolio_export_is_told_apart_from_a_transaction_export(string header)
    {
        var detection = ImportSourceDetector.Detect("depot.csv", Encoding.UTF8.GetBytes(header + "\n" + "01.03.2026;Kauf;DE0007164600;5;-512,40\n"));

        Assert.Equal(ImportSourceDetector.AdapterInvestments, detection.Adapter);
        Assert.Equal("investmentColumns", detection.ReasonKey);
    }

    [Fact]
    public void A_plain_bank_csv_becomes_the_generic_transaction_path_with_its_columns_already_guessed()
    {
        var detection = ImportSourceDetector.Detect("umsaetze.csv",
            Encoding.UTF8.GetBytes("Buchungstag;Betrag;Währung;Empfänger;Verwendungszweck\n01.03.2026;-12,34;EUR;REWE;Einkauf\n"));

        Assert.Equal(ImportSourceDetector.AdapterTransactions, detection.Adapter);
        Assert.Equal(1, detection.Rows);
        Assert.Equal("Buchungstag", detection.SuggestedMapping?.Date);
        Assert.Equal("Betrag", detection.SuggestedMapping?.Amount);
        Assert.Equal(["Buchungstag", "Betrag", "Währung", "Empfänger", "Verwendungszweck"], detection.Headers);
    }

    /// <summary>
    /// Was sich nicht belegen laesst, heisst <c>unknown</c>. Dann waehlt weiterhin der Nutzer - eine
    /// geratene Quelle waere schlechter als gar keine, weil sie den falschen Weg selbstbewusst oeffnet.
    /// </summary>
    [Fact]
    public void A_table_without_a_date_or_amount_column_stays_unknown()
    {
        var detection = ImportSourceDetector.Detect("liste.csv", Encoding.UTF8.GetBytes("Name;Ort\nMüller;Berlin\n"));

        Assert.Equal(ImportSourceDetector.AdapterUnknown, detection.Adapter);
        Assert.Equal("unmappedColumns", detection.ReasonKey);
        // Die Kopfzeilen kommen trotzdem mit: wer von Hand zuordnet, sieht sie sofort.
        Assert.Equal(["Name", "Ort"], detection.Headers);
    }

    [Fact]
    public void A_file_no_reader_understands_stays_unknown()
    {
        var detection = ImportSourceDetector.Detect("bild.png", [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        Assert.Equal(ImportSourceDetector.AdapterUnknown, detection.Adapter);
        Assert.Equal("unreadable", detection.ReasonKey);
    }

    /// <summary>
    /// Ein UTF-8-BOM gehoert zur Kodierung, nicht zum ersten Spaltennamen. Die zwei Importwege lasen das
    /// unterschiedlich: auf dem einen hiess die Spalte <c>Datum</c>, auf dem anderen
    /// <c>﻿Datum</c> - und dort fand die Spaltenerkennung sie nicht mehr.
    /// </summary>
    [Fact]
    public void A_byte_order_mark_is_not_part_of_the_first_column_name()
    {
        var detection = ImportSourceDetector.Detect("umsaetze.csv",
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Datum;Betrag\n01.03.2026;-1,00\n")).ToArray());

        Assert.Equal(ImportSourceDetector.AdapterTransactions, detection.Adapter);
        Assert.Equal("Datum", detection.SuggestedMapping?.Date);
    }

    private static readonly string[] FinanzguruHeaders =
    [
        "Buchungstag", "Betrag", "Waehrung", "Buchungs-ID", "Referenzkonto", "Name Referenzkonto",
        "Beguenstigter/Auftraggeber", "Verwendungszweck", "E-Ref", "Analyse-Hauptkategorie",
        "Analyse-Unterkategorie", "Analyse-Umbuchung", "Referenz-Original-ID", "Split-Typ"
    ];

    private static byte[] FinanzguruWorkbook(params (string Date, string Amount, string AccountName, string BookingId)[] rows) =>
        Workbook(FinanzguruHeaders, rows.Select(row => new[]
        {
            row.Date, row.Amount, "EUR", row.BookingId, "DE00", row.AccountName,
            "Shop", "Einkauf", "", "Lifestyle", "Shopping", "nein", "", ""
        }).ToArray());

    private static byte[] Workbook(IReadOnlyList<string> headers, params string[][] dataRows)
    {
        var ns = (XNamespace)"http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = new List<XElement> { Row(ns, 1, headers) };
        for (var index = 0; index < dataRows.Length; index++) rows.Add(Row(ns, index + 2, dataRows[index]));

        var document = new XDocument(new XElement(ns + "worksheet", new XElement(ns + "sheetData", rows)));
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("xl/worksheets/sheet1.xml");
            using var stream = entry.Open();
            document.Save(stream);
        }
        return output.ToArray();
    }

    private static XElement Row(XNamespace ns, int number, IReadOnlyList<string> values)
    {
        var cells = values.Select((value, index) => new XElement(ns + "c",
            new XAttribute("r", $"{ColumnName(index + 1)}{number}"),
            new XAttribute("t", "inlineStr"),
            new XElement(ns + "is", new XElement(ns + "t", value))));
        return new XElement(ns + "row", new XAttribute("r", number), cells);
    }

    private static string ColumnName(int column)
    {
        var builder = new StringBuilder();
        while (column > 0)
        {
            column--;
            builder.Insert(0, (char)('A' + column % 26));
            column /= 26;
        }
        return builder.ToString();
    }
}
