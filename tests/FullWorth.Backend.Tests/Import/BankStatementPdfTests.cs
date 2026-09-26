using FullWorth.Backend.Documents;
using FullWorth.Backend.Modules.Import;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Kontoauszuege als PDF (#131, Abschnitt 11). Die Auszuege hier sind erfunden - Namen, Betraege,
/// Kartennummer -, bilden aber den Aufbau der echten Ikano- und C24-Auszuege nach, an denen die Leser
/// entstanden sind: dieselben Beschriftungen, dieselbe Zeilenform, dieselben Fallen.
///
/// Die Falle, um die es vor allem geht: ein PDF wird nachgerechnet, nicht geglaubt.
/// </summary>
public sealed class BankStatementPdfTests
{
    /// <summary>
    /// Ein Ikano-Auszug im Aufbau des echten: Kartenumsaetze und Ratenkauf-Finanzierung, zwischen denen
    /// "Saldenkorrektur" und "Umbuchung in Ratenkauf" Betraege verschieben. Jeder Abschnitt fuer sich geht
    /// nicht auf, der Auszug als Ganzes auf den Cent: -1.000 + 50 + 20 - 120 + 120 - 20 - 120 + 20 = -1.050.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<PdfLine>> Ikano(
        string closing = "1.050,00 -", string available = "3.950,00", string purchaseSign = "-") =>
    [
        Page(
            "Abrechnung vom 24.08.2026 bis 23.09.2026",
            "Ikano Bank AB (publ), Postfach 1, 00000 Musterstadt",
            // Die IBAN im Kopf ist die des Girokontos, von dem abgebucht wird - nicht die des Ikano-Kontos.
            "Kreditkartennummer: 4000 12XX XXXX 1234 IBAN: DE00123456780000000000",
            "Vertrags-ID: 0001112223334445 Kreditrahmen: 5.000 EUR",
            "Datum: 23.09.2026 Verfügbarer Betrag: " + available + " EUR",
            "Gesamtsaldo alt: 1.000,00 -",
            "01.09.26 Lastschrifteinzug 50,00 +",
            "Umsätze",
            "Saldo alt: 0,00",
            "01.09.26 01.09.26 Automatische Saldenkorrektur 20,00 -",
            "12.09.26 12.09.26 UMBUCHUNG IN RATENKAUF - MOEBELHAUS 120,00 +",
            "Visa 4000 12XX XXXX 1234",
            "10.09.26 11.09.26 MOEBELHAUS BEISPIEL MUSTERSTADT DE 120,00 " + purchaseSign,
            "30.08.26 01.09.26 ONLINESHOP GUTSCHRIFT LU 20,00 +",
            "Saldo neu: 0,00"),
        Page(
            "Buchungen 0,00% Finanzierung",
            "Saldo alt: 1.000,00 -",
            "01.09.26 01.09.26 Automatische Saldenkorrektur 20,00 +",
            "12.09.26 12.09.26 RATENKAUF, 003 MONATE - MOEBELHAUS 120,00 -",
            // Ohne Vorzeichen: der Ratenplan, keine Buchung.
            "23.09.26 23.09.26 RATE MOEBELHAUS BEISPIEL (001/003) 40,00",
            "Saldo neu: 1.050,00 -",
            "Saldo gesamt: " + closing),
    ];

    [Fact]
    public void AnIkanoStatementThatReconcilesKeepsItsRealBookingsPreselected()
    {
        var statement = BankStatementPdf.Read(Ikano());

        Assert.Equal(BankStatementPdf.IkanoAdapter, statement.AdapterKey);
        Assert.Empty(statement.Warnings ?? []);
        Assert.NotNull(statement.ClosingBalance);
        Assert.Equal(-1_050m, statement.ClosingBalance!.Amount);
        Assert.Equal(new DateOnly(2026, 9, 23), statement.ClosingBalance.AsOf);

        // Die Ratenplan-Zeile ohne Vorzeichen ist keine Buchung.
        Assert.Equal(7, statement.Entries.Count);
        Assert.DoesNotContain(statement.Entries, entry => entry.Counterparty!.StartsWith("RATE MOEBELHAUS", StringComparison.Ordinal));

        // Die echten Bewegungen sind vorgewaehlt, die internen Umbuchungen nicht.
        var real = statement.Entries.Where(entry => entry.ReviewNote is null).ToList();
        Assert.Equal(3, real.Count);
        Assert.Equal(-50m, real.Sum(entry => entry.Amount));
        Assert.All(statement.Entries.Where(entry => entry.ReviewNote is not null),
            entry => Assert.Equal(BankStatementPdf.InternalTransfer, entry.ReviewNote));

        var purchase = Assert.Single(statement.Entries, entry => entry.Counterparty!.StartsWith("MOEBELHAUS BEISPIEL", StringComparison.Ordinal));
        Assert.Equal(-120m, purchase.Amount);
        Assert.Equal(new DateOnly(2026, 9, 11), purchase.BookingDate);
        Assert.Equal(new DateOnly(2026, 9, 10), purchase.ValueDate);
    }

    /// <summary>
    /// Ein einziges falsch gelesenes Vorzeichen, und der Auszug geht nicht mehr auf. Dann wird keine Zeile
    /// still uebernommen - jede steht zur Pruefung da, und die Datei sagt, warum.
    /// </summary>
    [Fact]
    public void OneMisreadSignPutsEveryRowToTheOwner()
    {
        var statement = BankStatementPdf.Read(Ikano(purchaseSign: "+"));

        Assert.Contains(BankStatementPdf.NotReconciled, statement.Warnings ?? []);
        Assert.All(statement.Entries, entry => Assert.Equal(BankStatementPdf.NotReconciled, entry.ReviewNote));
    }

    /// <summary>
    /// Die Kontokennung ist die Kartennummer. Die IBAN im Kopf gehoert zum Girokonto, von dem Ikano die
    /// Rate einzieht - mit ihr landete der Auszug auf dem falschen Konto.
    /// </summary>
    [Fact]
    public void TheAccountIsTheCardNotTheIbanInTheHeader()
    {
        var statement = BankStatementPdf.Read(Ikano());

        Assert.Equal("Ikano •••• 1234", statement.AccountIdentifier);
        Assert.DoesNotContain("DE00", statement.AccountIdentifier!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gehen die Zeilen nicht auf, traegt der Kreditrahmen den Saldo noch allein. Bestaetigt auch er ihn
    /// nicht, ist der Saldo so wenig gesichert wie die Zeilen - dann wird keiner uebernommen.
    /// </summary>
    [Fact]
    public void ABalanceNeitherTheRowsNorTheCreditLimitConfirmIsNotTaken()
    {
        var confirmedByLimit = BankStatementPdf.Read(Ikano(purchaseSign: "+"));
        Assert.Equal(-1_050m, confirmedByLimit.ClosingBalance!.Amount);

        var confirmedByNothing = BankStatementPdf.Read(Ikano(purchaseSign: "+", available: "3.900,00"));
        Assert.Null(confirmedByNothing.ClosingBalance);
        Assert.Contains(BankStatementPdf.NotReconciled, confirmedByNothing.Warnings ?? []);
        Assert.Contains(BankStatementPdf.CreditLimitMismatch, confirmedByNothing.Warnings ?? []);
    }

    [Fact]
    public void ACreditLimitThatDoesNotMatchTheBalanceIsShown()
    {
        var statement = BankStatementPdf.Read(Ikano(available: "3.900,00"));

        Assert.Contains(BankStatementPdf.CreditLimitMismatch, statement.Warnings ?? []);
        // Die Buchungen selbst gehen trotzdem auf - die Probe sperrt nichts, sie zeigt es nur.
        Assert.DoesNotContain(BankStatementPdf.NotReconciled, statement.Warnings ?? []);
    }

    private static IReadOnlyList<IReadOnlyList<PdfLine>> C24(
        string start = "250,00 €", string debits = "-0,00 €", string credits = "+0,00 €", string end = "250,00 €",
        string[]? rows = null, string period = "01.09.2026 - 25.09.2026") =>
    [
        Page([
            "C24 Smartkonto",
            "Girokonto",
            "IBAN: DE00999999990000000001",
            // Der Kontostand im Kopf ist der Endsaldo - steht dort etwas anderes, ist eine Zahl falsch gelesen.
            "Vorläufiger Kontoauszug 09/2026 Kontostand " + end,
            period,
            "Transaktionsübersicht",
            "Buchung Valuta Transaktionsinformation Betrag",
            .. rows ?? ["Keine Transaktionen im Zeitraum vorhanden"],
            "Zusammenfassung",
            "Startsaldo " + start,
            "Kontobelastungen " + debits,
            "Kontogutschriften " + credits,
            "Endsaldo " + end,
            "C24 Bank GmbH",
            "09/2026 Musterstraße 1, 00000 Musterstadt Seite 1 von 2"]),
    ];

    /// <summary>Drei Buchungen wie auf dem echten Auszug: Kartenzahlung, Ueberweisung, Erstattung.</summary>
    private static readonly string[] ThreeBookings =
    [
        "18.09. 18.09. Online-Kartenzahlung -15,00 €",
        "SHOP *BEISPIEL",
        "17.09. 16.09. Echtzeitüberweisung +40,00 €",
        "Erika Musterfrau",
        "Urlaub",
        "IBAN: DE00999999990000000002 / BIC: TESTDEFF",
        "12.09. 12.09. Rückerstattung +5,00 €",
        "Versandhaus",
    ];

    [Fact]
    public void AC24StatementWithoutBookingsAnchorsTheBalance()
    {
        var statement = BankStatementPdf.Read(C24());

        Assert.Equal(BankStatementPdf.C24Adapter, statement.AdapterKey);
        Assert.Empty(statement.Entries);
        Assert.Empty(statement.Warnings ?? []);
        Assert.Equal(250.00m, statement.ClosingBalance!.Amount);
        Assert.Equal(new DateOnly(2026, 9, 25), statement.ClosingBalance.AsOf);
        Assert.Equal("DE00999999990000000001", statement.AccountIdentifier);
    }

    /// <summary>
    /// Die Buchungen eines C24-Auszugs: Datum ohne Jahr (es kommt aus dem Zeitraum), die Gegenseite aus
    /// der ersten Zeile der Einzelheiten, der Rest samt Art der Buchung als Beschreibung. Sie gehen auf
    /// den Cent auf - also wird keine zur Pruefung vorgelegt.
    /// </summary>
    [Fact]
    public void AC24StatementReadsItsBookings()
    {
        var statement = BankStatementPdf.Read(C24(debits: "-15,00 €", credits: "+45,00 €", end: "280,00 €", rows: ThreeBookings));

        Assert.Empty(statement.Warnings ?? []);
        Assert.Equal(280.00m, statement.ClosingBalance!.Amount);
        Assert.Collection(statement.Entries,
            card =>
            {
                Assert.Equal(new DateOnly(2026, 9, 18), card.BookingDate);
                Assert.Equal(-15.00m, card.Amount);
                Assert.Equal("SHOP *BEISPIEL", card.Counterparty);
                Assert.Equal("Online-Kartenzahlung", card.Description);
                Assert.Null(card.ReviewNote);
            },
            transfer =>
            {
                Assert.Equal(new DateOnly(2026, 9, 17), transfer.BookingDate);
                Assert.Equal(new DateOnly(2026, 9, 16), transfer.ValueDate);
                Assert.Equal(40.00m, transfer.Amount);
                Assert.Equal("Erika Musterfrau", transfer.Counterparty);
                Assert.Equal("Echtzeitüberweisung · Urlaub · IBAN: DE00999999990000000002 / BIC: TESTDEFF", transfer.Description);
            },
            refund =>
            {
                Assert.Equal(5.00m, refund.Amount);
                Assert.Equal("Versandhaus", refund.Counterparty);
            });
    }

    /// <summary>
    /// Die Summe allein genuegt nicht: -20 und +8 ergeben dieselben -12 wie eine einzige Belastung. Ein
    /// falsch gelesenes Vorzeichen faellt erst an Belastungen und Gutschriften je fuer sich auf.
    /// </summary>
    [Fact]
    public void AC24StatementWhoseSidesDoNotMatchPutsEveryRowToReview()
    {
        var statement = BankStatementPdf.Read(C24(debits: "-12,00 €", end: "238,00 €", rows:
        [
            "03.09. 03.09. Lastschrift -20,00 €",
            "Stadtwerke",
            "04.09. 04.09. Gutschrift +8,00 €",
            "Erika Musterfrau",
        ]));

        Assert.Contains(BankStatementPdf.NotReconciled, statement.Warnings ?? []);
        Assert.All(statement.Entries, entry => Assert.Equal(BankStatementPdf.NotReconciled, entry.ReviewNote));
        // Der Kontostand haengt an Zusammenfassung und Kopf - die stimmen ueberein, er kommt an.
        Assert.Equal(238.00m, statement.ClosingBalance!.Amount);
    }

    /// <summary>
    /// Ein Auszug von Dezember bis Januar: "20.12." gehoert ins alte Jahr, "05.01." ins neue.
    /// </summary>
    [Fact]
    public void AC24StatementAcrossTheTurnOfTheYearDatesEachBookingInItsYear()
    {
        var statement = BankStatementPdf.Read(C24(debits: "-10,00 €", credits: "+10,00 €", period: "15.12.2026 - 14.01.2027", rows:
        [
            "20.12. 20.12. Online-Kartenzahlung -10,00 €",
            "SHOP *BEISPIEL",
            "05.01. 05.01. Gutschrift +10,00 €",
            "Erika Musterfrau",
        ]));

        Assert.Equal(new DateOnly(2026, 12, 20), statement.Entries[0].BookingDate);
        Assert.Equal(new DateOnly(2027, 1, 5), statement.Entries[1].BookingDate);
    }

    /// <summary>
    /// Geht die Tabelle auf der naechsten Seite weiter, stehen dazwischen Seitenfuss und die Anschrift
    /// der Folgeseite. Keines davon darf in die Einzelheiten der letzten Buchung geraten.
    /// </summary>
    [Fact]
    public void AC24TableOverTwoPagesKeepsFooterAndAddressOutOfTheBookings()
    {
        var first = C24(debits: "-15,00 €", credits: "+45,00 €", end: "280,00 €", rows: ThreeBookings[..6])[0]
            .Where(line => !line.Text.StartsWith("Zusammenfassung") && !line.Text.StartsWith("Startsaldo")
                && !line.Text.StartsWith("Kontobelastungen") && !line.Text.StartsWith("Kontogutschriften")
                && !line.Text.StartsWith("Endsaldo"))
            .ToList();
        var second = Page([
            "Erika Musterfrau",
            "Musterweg 2",
            "C24 Smartkonto",
            "Buchung Valuta Transaktionsinformation Betrag",
            .. ThreeBookings[6..],
            "Zusammenfassung",
            "Startsaldo 250,00 €",
            "Kontobelastungen -15,00 €",
            "Kontogutschriften +45,00 €",
            "Endsaldo 280,00 €",
            "C24 Bank GmbH",
            "09/2026 Musterstraße 1, 00000 Musterstadt Seite 2 von 2"]);

        var statement = BankStatementPdf.Read([first, second]);

        Assert.Empty(statement.Warnings ?? []);
        Assert.Equal(3, statement.Entries.Count);
        Assert.Equal("Echtzeitüberweisung · Urlaub · IBAN: DE00999999990000000002 / BIC: TESTDEFF", statement.Entries[1].Description);
        Assert.Equal("Versandhaus", statement.Entries[2].Counterparty);
    }

    /// <summary>
    /// Hat der Auszug Buchungen in einem Format, das der Leser nicht kennt, liest er sie nicht halb: er
    /// sagt es, und nur der Kontostand kommt an.
    /// </summary>
    [Fact]
    public void AC24StatementWithUnreadableBookingsSaysSo()
    {
        var statement = BankStatementPdf.Read(C24(debits: "-12,00 €", end: "238,00 €", rows: ["Irgendeine Buchung in neuem Format 12,00"]));

        Assert.Contains(BankStatementPdf.RowsNotRead, statement.Warnings ?? []);
        Assert.Empty(statement.Entries);
        Assert.Equal(238.00m, statement.ClosingBalance!.Amount);
    }

    /// <summary>
    /// Stimmt die Zusammenfassung nicht, ist eine ihrer Zahlen falsch gelesen - und ein falscher Kontostand
    /// ist schlimmer als keiner.
    /// </summary>
    [Fact]
    public void AC24SummaryThatDoesNotAddUpAnchorsNothing()
    {
        var statement = BankStatementPdf.Read(C24(end: "260,00 €"));

        Assert.Contains(BankStatementPdf.NotReconciled, statement.Warnings ?? []);
        Assert.Null(statement.ClosingBalance);
    }

    [Fact]
    public void AnUnknownPdfIsAnErrorNotAnEmptyStatement()
    {
        Assert.Throws<InvalidDataException>(() => BankStatementPdf.Read([Page("Irgendeine Rechnung", "Summe 12,00")]));
    }

    internal static IReadOnlyList<PdfLine> Page(params string[] lines) =>
        lines.Select((text, index) => new PdfLine(20 + index * 18, [new PdfWord(40, 14 + index * 18, 500, 26 + index * 18, text)]))
            .ToList();
}
