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
        var merged = HitansFixture.Merge(Hitans(4, "942"), Hitans(6, "942"));

        var method = Assert.Single(merged.TanMethods);
        Assert.Equal("942", method.SecurityFunction);
        Assert.Equal(6, method.SegmentVersion);
    }

    [Fact]
    public void AndTheOrderInTheAnswerDoesNotDecideIt()
    {
        var merged = HitansFixture.Merge(Hitans(7, "942"), Hitans(4, "942"));

        Assert.Equal(7, Assert.Single(merged.TanMethods).SegmentVersion);
    }

    [Fact]
    public void DifferentSecurityFunctionsStayApart()
    {
        var merged = HitansFixture.Merge(Hitans(6, "942"), Hitans(6, "944"));

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
    /// Eine Version, deren Aufbau dieser Code nicht schreiben kann, wird nicht behauptet - und auch
    /// nicht auf 6 hochgebogen.
    ///
    /// Genau das tat die alte Untergrenze: aus jeder Ankuendigung wurde eine 6. ING kuendigt eine
    /// aeltere Version an und kennt HKTAN #6 nicht; die Antwort war "9010@5 HKTAN Der gewuenschte
    /// Geschaeftsvorfall wird nicht unterstuetzt". Wer keine starke Authentifizierung bei der
    /// Anmeldung anbietet, bekommt auch kein HKTAN.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task AVersionWhoseLayoutThisCodeCannotWriteIsNeverSent(int announced)
        => Assert.Null(await SentHkTanOrNullAsync(announced));

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

    /// <summary>Das HKTAN, das bei <c>OpenAsync</c> tatsaechlich auf die Leitung geht.</summary>
    private static async Task<FinTsSegment?> SentHkTanOrNullAsync(int announcedVersion, string? medium = null)
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

        Assert.Single(transport.Messages);
        return transport.Sent().Find("HKTAN");
    }

    /// <summary>Wie oben, aber es MUSS eines geben.</summary>
    private static async Task<FinTsSegment> SentHkTanAsync(int announcedVersion, string? medium = null)
    {
        var hktan = await SentHkTanOrNullAsync(announcedVersion, medium);
        Assert.NotNull(hktan);
        return hktan!;
    }

    private static FinTsBankParameters Empty => new(0, 0, "0", "999", null,
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
        [], []);


    private static FinTsSegment Hitans(int version, string securityFunction)
        => HitansFixture.Segment(version, HitansFixture.Method(Math.Max(version, 6), securityFunction, "pushTAN"));
}
