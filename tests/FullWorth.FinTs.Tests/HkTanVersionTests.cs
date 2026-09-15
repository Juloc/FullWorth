using System.Globalization;
using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// HKTAN geht in der Version raus, die die Bank angekuendigt hat - und traegt den Aufbau, der zu
/// dieser Version gehoert (#130 §8).
///
/// ING wies jede Anmeldung mit "9800 Der Dialog wurde abgebrochen" zurueck. Erst der Bauplan der
/// gesendeten Nachricht zeigte, warum:
///
/// <code>
/// AllCodes=9800 Der Dialog wurde abgebrochen.; 9110 Unbekannter Aufbau der Kundennachricht.
/// SentShape=HKIDN:v2:4, HKVVB:v3:5, HKTAN:v4:2
/// </code>
///
/// Versionskopf 4, zwei Datenelemente. Zwei Fehler griffen ineinander:
///
/// 1. Eine Bank kuendigt dasselbe TAN-Verfahren in MEHREREN Segmentversionen an. Das Zusammenfuehren
///    behielt je Sicherheitsfunktion das ZUERST gesehene - und das ist das aelteste. HITANS:6 wurde
///    weggeworfen, HITANS:4 blieb stehen.
/// 2. Gebaut wurde daraufhin ein Segment mit Versionskopf 4, aber mit dem Aufbau ab Version 6: die
///    Segmentkennung "HKIDN" an Stelle 2. Die gibt es vor Version 5 nicht - dort steht an Stelle 2
///    der Auftrags-Hashwert. Ein Segmentname in einem Hashfeld ist genau das, was eine Bank mit
///    "9110 Unbekannter Aufbau der Kundennachricht" beantwortet.
/// </summary>
public sealed class HkTanVersionTests
{
    private static readonly FinTsBankProfile Bank = new(
        "ing", "ING", "50010517", "INGDDEFFXXX", new Uri("https://fints.example/fints"), new HashSet<FinTsCapability>());

    private static readonly FinTsCredentials Credentials = new("user", "pin", "PRODUCT01");

    [Fact]
    public void TheHighestAnnouncedVersionWinsNotTheFirstSeen()
    {
        var merged = Merge(Hitans(4, "942", "pushTAN"), Hitans(6, "942", "pushTAN"));

        var method = Assert.Single(merged.TanMethods);
        Assert.Equal("942", method.SecurityFunction);
        Assert.Equal(6, method.SegmentVersion);
    }

    [Fact]
    public void AndTheOrderInTheAnswerDoesNotDecideIt()
    {
        var merged = Merge(Hitans(7, "942", "pushTAN"), Hitans(4, "942", "pushTAN"));

        Assert.Equal(7, Assert.Single(merged.TanMethods).SegmentVersion);
    }

    [Fact]
    public void DifferentSecurityFunctionsStayApart()
    {
        var merged = Merge(Hitans(6, "942", "pushTAN"), Hitans(6, "944", "photoTAN"));

        Assert.Equal(2, merged.TanMethods.Count);
    }

    /// <summary>Was die Bank ankuendigt, steht auch im Kopf der gesendeten Nachricht.</summary>
    [Theory]
    [InlineData(6, 6)]
    [InlineData(7, 7)]
    public async Task TheAnnouncedVersionReachesTheWire(int announced, int expected)
    {
        var hktan = await SentHkTanAsync(announced);

        Assert.Equal(expected, hktan.Version);
    }

    /// <summary>
    /// Eine Version, deren Aufbau dieser Code gar nicht schreiben kann, wird nicht behauptet. Vorher
    /// stand hier eine Untergrenze von 4 - genau die 4 aus dem Protokoll.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task AVersionWhoseLayoutThisCodeCannotWriteIsNeverSent(int announced)
    {
        var hktan = await SentHkTanAsync(announced);

        Assert.Equal(6, hktan.Version);
    }

    /// <summary>
    /// Der Aufbau ab Version 6, nach Stellen: 1 TAN-Prozess, 2 Segmentkennung, 5 Auftragsreferenz,
    /// 11 Bezeichnung des TAN-Mediums. Genau daran haengt, ob die Bank die Nachricht versteht - die
    /// Zahl der Datenelemente allein sagt es nicht.
    /// </summary>
    [Fact]
    public async Task TheSegmentNameSitsAtPositionTwoAndTheMediumAtEleven()
    {
        var hktan = await SentHkTanAsync(6, medium: "TAN2go");

        Assert.Equal("4", hktan.GetText(1, 0));
        Assert.Equal("HKIDN", hktan.GetText(2, 0));
        Assert.Equal("TAN2go", hktan.GetText(11, 0));
        Assert.Equal(11, hktan.Groups.Count - 1);
    }

    /// <summary>Ohne TAN-Medium endet das Segment nach Stelle 7 - kein leeres Feld ohne Grund.</summary>
    [Fact]
    public async Task WithoutATanMediumTheSegmentEndsAfterTheSeventhElement()
    {
        var hktan = await SentHkTanAsync(6);

        Assert.Equal(7, hktan.Groups.Count - 1);
    }

    private static FinTsBankParameters Merge(params FinTsSegment[] segments)
        => FinTsResponseParser.MergeParameters(Empty, FinTsResponseParser.Parse(FinTsWire.Serialize(segments)));

    /// <summary>Das HKTAN, das bei <c>OpenAsync</c> tatsaechlich auf die Leitung geht.</summary>
    private static async Task<FinTsSegment> SentHkTanAsync(int announcedVersion, string? medium = null)
    {
        var transport = new CapturingTransport();
        var parameters = Empty with
        {
            SystemId = "1234",
            SecurityFunction = "942",
            TanMedium = medium,
            TanMethods = [new FinTsTanMethod("942", "pushTAN", "2", medium is not null, false, 0, 0, 0, announcedVersion)]
        };

        await new FinTsClient(transport).OpenAsync(Bank, Credentials, parameters);

        var sent = FinTsResponseParser.Parse(Assert.Single(transport.Messages));
        var hktan = sent.Find("HKTAN");
        Assert.NotNull(hktan);
        return hktan!;
    }

    private static FinTsBankParameters Empty => new(0, 0, "0", "999", null,
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
        [], []);

    /// <summary>Ein HITANS-Segment mit genau einem Verfahren, so wie eine Bank es ankuendigt.</summary>
    private static FinTsSegment Hitans(int version, string securityFunction, string name)
    {
        var method = new List<FinTsValue>();
        for (var i = 0; i < 25; i++) method.Add(FinTsValue.E());
        method[0] = FinTsValue.T(securityFunction);
        method[1] = FinTsValue.T("2");
        method[version >= 6 ? 3 : 2] = FinTsValue.T(name);

        return new FinTsSegment([
            FinTsGroup.Of(
                FinTsValue.T("HITANS"),
                FinTsValue.T("5"),
                FinTsValue.T(version.ToString(CultureInfo.InvariantCulture)),
                FinTsValue.T("4")),
            FinTsGroup.Of(FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("0")),
            new FinTsGroup(method)
        ]);
    }

    /// <summary>Haelt fest, was gesendet wurde, und antwortet mit einem erfolgreichen Dialog.</summary>
    private sealed class CapturingTransport : IFinTsTransport
    {
        public List<byte[]> Messages { get; } = [];

        public Task<byte[]> SendAsync(Uri endpoint, byte[] message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult(FinTsWire.Serialize([
                new FinTsSegment([
                    FinTsGroup.Of(FinTsValue.T("HNHBK"), FinTsValue.T("1"), FinTsValue.T("3")),
                    FinTsGroup.Of(FinTsValue.T("000000000300")),
                    FinTsGroup.Of(FinTsValue.T("300")),
                    FinTsGroup.Of(FinTsValue.T("DIALOG01"))
                ]),
                new FinTsSegment([
                    FinTsGroup.Of(FinTsValue.T("HIRMG"), FinTsValue.T("2"), FinTsValue.T("2")),
                    FinTsGroup.Of(FinTsValue.T("0010"), FinTsValue.T("-"), FinTsValue.T("Nachricht entgegengenommen."))
                ])
            ]));
        }
    }
}
