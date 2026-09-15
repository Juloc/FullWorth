using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Das Sicherheitsprofil im Signaturkopf sagt dasselbe wie die Sicherheitsfunktion daneben.
///
/// Nachdem HKTAN in der richtigen Version ankam, las ING die Nachricht - und antwortete:
///
/// <code>
/// AllCodes=9800 Der Dialog wurde abgebrochen.; 9010 Ungueltiger Signaturaufbau: Fehler im Segmentaufbau.
/// SentShape=HKIDN:v2:4, HKVVB:v3:5, HKTAN:v6:7
/// </code>
///
/// Das Sicherheitsprofil an Stelle 1 von HNSHK hat zwei Teile: das Verfahren ("PIN") und SEINE
/// Version - 1 fuer das Einschritt-, 2 fuer das Zwei-Schritt-Verfahren. Dort stand fest die 1, auch
/// wenn an Stelle 2 eine Zwei-Schritt-Sicherheitsfunktion stand.
///
/// Warum das so lange unsichtbar war, sagt der Vergleich der beiden Dialoge: der
/// Synchronisationsdialog laeuft mit Sicherheitsfunktion 999, dem Einschritt-Verfahren - dort ist die
/// 1 richtig, und er ging immer durch. Erst der Anmeldedialog schickt die echte Sicherheitsfunktion.
/// Genau dieser Unterschied ist der Beweis: derselbe Signaturblock, einmal angenommen, einmal
/// abgelehnt, und dazwischen liegt nur die Sicherheitsfunktion.
/// </summary>
public sealed class SignatureHeaderTests
{
    private static readonly FinTsBankProfile Bank = new(
        "ing", "ING", "50010517", "INGDDEFFXXX", new Uri("https://fints.example/fints"), new HashSet<FinTsCapability>());

    private static readonly FinTsCredentials Credentials = new("user", "pin", "PRODUCT01");

    /// <summary>Der Synchronisationsdialog: Einschritt-Verfahren, Profilversion 1.</summary>
    [Fact]
    public async Task TheSynchronisationDialogSignsAsOneStep()
    {
        var transport = new CapturingTransport();

        await new FinTsClient(transport).SynchronizeAsync(Bank, Credentials);

        var signature = Signature(transport);
        Assert.Equal("PIN", signature.GetText(1, 0));
        Assert.Equal("1", signature.GetText(1, 1));
        Assert.Equal("999", signature.GetText(2, 0));
    }

    /// <summary>
    /// Verschluesselungskopf und Signaturkopf beschreiben die Sicherheit derselben Nachricht - sie
    /// sagen dasselbe ueber sie.
    ///
    /// Das war der zweite Anlauf an derselben Meldung: HNSHK trug schon die 2, HNVSK noch die fest
    /// verdrahtete 1, und die Nachricht widersprach sich in sich selbst. ING blieb bei
    /// "9010 Ungueltiger Signaturaufbau".
    /// </summary>
    [Theory]
    [InlineData("999", "1")]
    [InlineData("942", "2")]
    public async Task BothSecurityHeadsCarryTheSameProcedureVersion(string securityFunction, string expected)
    {
        var transport = new CapturingTransport();
        var client = new FinTsClient(transport);

        if (securityFunction == FinTsMessages.OneStepSecurityFunction)
            await client.SynchronizeAsync(Bank, Credentials);
        else
            await client.OpenAsync(Bank, Credentials, TwoStep(securityFunction));

        var sent = transport.Sent();
        var encryption = sent.Find("HNVSK");
        Assert.NotNull(encryption);
        Assert.Equal("PIN", encryption!.GetText(1, 0));
        Assert.Equal(expected, encryption.GetText(1, 1));
        Assert.Equal(expected, Signature(transport).GetText(1, 1));
    }

    /// <summary>
    /// Der Anmeldedialog: Zwei-Schritt-Verfahren, also Profilversion 2. Hier stand vorher die 1, und
    /// genau diese Mischung beantwortet ING mit 9010.
    /// </summary>
    [Theory]
    [InlineData("942")]
    [InlineData("944")]
    [InlineData("962")]
    public async Task TheLoginDialogSignsAsTwoStep(string securityFunction)
    {
        var transport = new CapturingTransport();

        await new FinTsClient(transport).OpenAsync(Bank, Credentials, TwoStep(securityFunction));

        var signature = Signature(transport);
        Assert.Equal("2", signature.GetText(1, 1));
        Assert.Equal(securityFunction, signature.GetText(2, 0));
    }

    /// <summary>
    /// Der Bauplan im Protokoll zeigt die ganze Nachricht, nicht nur die Auftraege. Ohne Umschlag und
    /// Signaturblock zeigte er bei "Ungueltiger Signaturaufbau" auf alles ausser die Stelle, um die es
    /// ging.
    /// </summary>
    [Fact]
    public async Task TheLoggedShapeCoversTheEnvelopeAndTheSignatureBlock()
    {
        var transport = new CapturingTransport();

        var error = await Assert.ThrowsAsync<FinTsException>(
            () => new FinTsClient(new RejectingTransport(transport)).OpenAsync(Bank, Credentials, TwoStep("942")));

        foreach (var expected in new[] { "HNHBK", "HNVSK", "HNSHK", "HKIDN", "HKVVB", "HKTAN", "HNSHA", "HNHBS" })
            Assert.Contains(expected, error.SentShapeSummary);
        // Struktur, keine Werte: die PIN steht im Signaturabschluss und darf nirgends auftauchen.
        Assert.DoesNotContain("pin", error.SentShapeSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("=", error.SentShapeSummary);
    }

    private static FinTsSegment Signature(CapturingTransport transport)
    {
        var hnshk = transport.Sent().Find("HNSHK");
        Assert.NotNull(hnshk);
        return hnshk!;
    }

    /// <summary>Eine Installation, die ein Zwei-Schritt-Verfahren gelernt hat.</summary>
    private static FinTsBankParameters TwoStep(string securityFunction) => new(
        0, 0, "1234", securityFunction, null,
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
        [new FinTsTanMethod(securityFunction, "pushTAN", "2", false, false, 0, 0, 0, 6)], []);

    /// <summary>Antwortet wie ING im Fehlerfall: 9800 als Sammelmeldung, 9010 als Grund.</summary>
    private sealed class RejectingTransport(CapturingTransport inner) : IFinTsTransport
    {
        public Task<byte[]> SendAsync(Uri endpoint, byte[] message, CancellationToken cancellationToken)
        {
            inner.SendAsync(endpoint, message, cancellationToken);
            return Task.FromResult(FinTsWire.Serialize([
                new FinTsSegment([
                    FinTsGroup.Of(FinTsValue.T("HNHBK"), FinTsValue.T("1"), FinTsValue.T("3")),
                    FinTsGroup.Of(FinTsValue.T("000000000300")),
                    FinTsGroup.Of(FinTsValue.T("300")),
                    FinTsGroup.Of(FinTsValue.T("0"))
                ]),
                new FinTsSegment([
                    FinTsGroup.Of(FinTsValue.T("HIRMG"), FinTsValue.T("2"), FinTsValue.T("2")),
                    FinTsGroup.Of(FinTsValue.T("9800"), FinTsValue.T("-"), FinTsValue.T("Der Dialog wurde abgebrochen.")),
                    FinTsGroup.Of(FinTsValue.T("9010"), FinTsValue.T("-"), FinTsValue.T("Ungueltiger Signaturaufbau."))
                ])
            ]));
        }
    }
}
