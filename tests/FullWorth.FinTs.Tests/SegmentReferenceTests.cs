using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Die Bank sagt, WELCHES Segment sie ablehnt - und das steht jetzt im Protokoll.
///
/// ING antwortete:
///
/// <code>
/// AllCodes=9050 Nachricht teilweise fehlerhaft.; 9010 Der gewuenschte Geschaeftsvorfall wird nicht unterstuetzt.
/// SegmentReference=-
/// </code>
///
/// "9050 Teilweise fehlerhaft" heisst laut Rueckmeldungscode-Katalog: in der Nachricht ist mindestens
/// ein fehlerhafter AUFTRAG enthalten. Also genau eines von HKIDN, HKVVB, HKTAN - und welches, stand
/// da nicht.
///
/// Der Grund: das Bezugssegment steht im SEGMENTKOPF von HIRMS an Stelle 4 (FinTS 3.0, Segmentkopf,
/// DE "Bezugssegment" - nur in der Kreditinstitutsnachricht belegt), nicht bei der Rueckmeldung
/// selbst. Gelesen wurde bisher nur das Bezugs-Datenelement innerhalb der Rueckmeldung, und das ist
/// bei Segmentfehlern leer.
///
/// Die blosse Nummer hilft niemandem, deshalb wird sie gegen den Bauplan der gesendeten Nachricht
/// gehalten: aus "5" wird "5 HKTAN".
/// </summary>
public sealed class SegmentReferenceTests
{
    private static readonly FinTsBankProfile Bank = new(
        "ing", "ING", "50010517", "INGDDEFFXXX", new Uri("https://fints.example/fints"), new HashSet<FinTsCapability>());

    private static readonly FinTsCredentials Credentials = new("user", "pin", "PRODUCT01");

    /// <summary>
    /// Der Weg von Ende zu Ende: die Bank bezieht sich auf Segment 5, und im Protokoll steht, dass
    /// das HKTAN ist.
    /// </summary>
    [Theory]
    [InlineData(3, "HKIDN")]
    [InlineData(4, "HKVVB")]
    [InlineData(5, "HKTAN")]
    public async Task TheRejectedSegmentIsNamedNotJustNumbered(int number, string expected)
    {
        var error = await Assert.ThrowsAsync<FinTsException>(
            () => new FinTsClient(new RejectingTransport(number)).OpenAsync(Bank, Credentials, TwoStep()));

        Assert.Equal($"{number} {expected}", error.SegmentReference);
        Assert.Contains($"9010@{number} {expected}", error.BankCodeSummary);
    }

    /// <summary>
    /// Der Bauplan traegt die Segmentnummern mit - ohne sie ist die Nummer der Bank nicht aufloesbar.
    /// Werte stehen weiterhin keine darin.
    /// </summary>
    [Fact]
    public async Task TheShapeCarriesTheSegmentNumbers()
    {
        var error = await Assert.ThrowsAsync<FinTsException>(
            () => new FinTsClient(new RejectingTransport(5)).OpenAsync(Bank, Credentials, TwoStep()));

        Assert.Contains("HNSHK#2:v4:11", error.SentShapeSummary);
        Assert.Contains("HKIDN#3:v2:4", error.SentShapeSummary);
        Assert.Contains("HKVVB#4:v3:5", error.SentShapeSummary);
        Assert.Contains("HKTAN#5:v6:7", error.SentShapeSummary);
        Assert.DoesNotContain("=", error.SentShapeSummary);
    }

    /// <summary>
    /// Eine Rueckmeldung ohne Bezugssegment - HIRMG gilt fuer die ganze Nachricht - erfindet keinen
    /// Bezug.
    /// </summary>
    [Fact]
    public async Task AMessageWideCodeGetsNoInventedSegment()
    {
        var error = await Assert.ThrowsAsync<FinTsException>(
            () => new FinTsClient(new RejectingTransport(null)).OpenAsync(Bank, Credentials, TwoStep()));

        Assert.Null(error.SegmentReference);
        Assert.Contains("9010 Der gewuenschte", error.BankCodeSummary);
        Assert.DoesNotContain("@", error.BankCodeSummary);
    }

    private static FinTsBankParameters TwoStep() => new(
        0, 0, "1234", "942", null,
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
        [new FinTsTanMethod("942", "pushTAN", "2", false, false, 0, 0, 0, 6)], []);

    /// <summary>
    /// Antwortet wie ING: 9050 fuer die Nachricht (HIRMG, ohne Bezug) und 9010 fuer ein Segment
    /// (HIRMS, mit Bezugssegment im Kopf).
    /// </summary>
    private sealed class RejectingTransport(int? rejectedSegment) : IFinTsTransport
    {
        public Task<byte[]> SendAsync(Uri endpoint, byte[] message, CancellationToken cancellationToken)
        {
            var segments = new List<FinTsSegment>
            {
                new([
                    FinTsGroup.Of(FinTsValue.T("HNHBK"), FinTsValue.T("1"), FinTsValue.T("3")),
                    FinTsGroup.Of(FinTsValue.T("000000000300")),
                    FinTsGroup.Of(FinTsValue.T("300")),
                    FinTsGroup.Of(FinTsValue.T("0"))
                ]),
                new([
                    FinTsGroup.Of(FinTsValue.T("HIRMG"), FinTsValue.T("2"), FinTsValue.T("2")),
                    FinTsGroup.Of(FinTsValue.T("9050"), FinTsValue.T("-"), FinTsValue.T("Nachricht teilweise fehlerhaft."))
                ])
            };

            // Das Bezugssegment ist die VIERTE Stelle des Segmentkopfs, nicht Teil der Rueckmeldung.
            var header = rejectedSegment is { } number
                ? FinTsGroup.Of(FinTsValue.T("HIRMS"), FinTsValue.T("3"), FinTsValue.T("2"), FinTsValue.T(number.ToString()))
                : FinTsGroup.Of(FinTsValue.T("HIRMS"), FinTsValue.T("3"), FinTsValue.T("2"));
            segments.Add(new([
                header,
                FinTsGroup.Of(FinTsValue.T("9010"), FinTsValue.T(""), FinTsValue.T("Der gewuenschte Geschaeftsvorfall wird nicht unterstuetzt."))
            ]));

            return Task.FromResult(FinTsWire.Serialize(segments));
        }
    }
}
