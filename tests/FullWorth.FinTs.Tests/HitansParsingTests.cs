using System.Globalization;
using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// HITANS wird gelesen, nicht geraten (#130 §8).
///
/// Im Protokoll stand bei ING:
///
/// <code>
/// ING FinTS announced. SecurityFunction=900, TanMethods=J:v1:0, SegmentVersions=...
/// </code>
///
/// <c>J</c> ist keine Sicherheitsfunktion. Eine Sicherheitsfunktion ist dreistellig und liegt zwischen
/// 900 und 997; <c>J</c> ist das Ja/Nein-Kennzeichen "Einschritt-Verfahren erlaubt". Die Auslegung
/// nahm jede Datenelementgruppe ab Nummer 4 fuer ein TAN-Verfahren und ihr erstes Feld fuer die
/// Sicherheitsfunktion.
///
/// Tatsaechlich ist Stelle 5 von HITANS EIN Parameterblock (FinTS 3.0, Security - Sicherheitsverfahren
/// PIN/TAN, Data-Dictionary):
///
/// <code>
/// 1 Einschritt-Verfahren erlaubt (J/N)
/// 2 Mehr als ein TAN-pflichtiger Auftrag pro Nachricht erlaubt (J/N)
/// 3 Auftrags-Hashwertverfahren
/// 4 Verfahrensparameter Zwei-Schritt-Verfahren - 1..98 WIEDERHOLUNGEN, je 21 Felder bei #6
/// </code>
///
/// Alle Verfahren stehen also hintereinander in einer Gruppe. Damit waren nicht nur die Namen falsch,
/// sondern jedes echte Verfahren verloren.
/// </summary>
public sealed class HitansParsingTests
{
    private static readonly FinTsBankProfile Bank = new(
        "ing", "ING", "50010517", "INGDDEFFXXX", new Uri("https://fints.example/fints"), new HashSet<FinTsCapability>());

    private static readonly FinTsCredentials Credentials = new("user", "pin", "PRODUCT01");

    [Fact]
    public void EveryAnnouncedMethodIsFoundNotJustTheParameterBlockItself()
    {
        var merged = HitansFixture.Merge(HitansFixture.Segment(6, HitansFixture.Method(6, "942", "pushTAN"), HitansFixture.Method(6, "944", "photoTAN")));

        Assert.Equal(2, merged.TanMethods.Count);
        Assert.Equal(["942", "944"], merged.TanMethods.Select(x => x.SecurityFunction));
        Assert.Equal(["pushTAN", "photoTAN"], merged.TanMethods.Select(x => x.Name));
        // Genau das stand vorher da: das Kennzeichen aus Stelle 1 des Parameterblocks.
        Assert.DoesNotContain(merged.TanMethods, x => x.SecurityFunction == "J");
    }

    /// <summary>Der Name steht an Stelle 6, nicht an Stelle 4 - dort steht das DK-Verfahren.</summary>
    [Fact]
    public void TheNameComesFromItsOwnFieldAndNotFromTheProcedureIdentifier()
    {
        var merged = HitansFixture.Merge(HitansFixture.Segment(6, HitansFixture.Method(6, "942", "pushTAN", procedure: "HHD")));

        Assert.Equal("pushTAN", Assert.Single(merged.TanMethods).Name);
    }

    /// <summary>
    /// Ein decoupled-Verfahren erkennt man am DK-Verfahren an Stelle 4, nicht an einem Ja/Nein-Feld.
    /// Die Wartezeiten stehen in #7 an den Stellen 22 bis 24.
    /// </summary>
    [Fact]
    public void DecoupledIsReadFromTheProcedureIdentifierTogetherWithItsPollingTimes()
    {
        var merged = HitansFixture.Merge(HitansFixture.Segment(7, HitansFixture.Method(7, "962", "pushTAN decoupled", procedure: "Decoupled", polls: (5, 10, 15))));

        var method = Assert.Single(merged.TanMethods);
        Assert.True(method.IsDecoupled);
        Assert.Equal(5, method.MaxPolls);
        Assert.Equal(10, method.WaitBeforeFirstPollSeconds);
        Assert.Equal(15, method.WaitBeforeNextPollSeconds);
    }

    /// <summary>
    /// Das TAN-Medium ist nur zu benennen, wenn die Bank es verlangt UND mehr als eines kennt -
    /// so steht es in der Restriktion zu HKTAN Stelle 12.
    /// </summary>
    [Theory]
    [InlineData("2", "3", true)]
    [InlineData("2", "1", false)]
    [InlineData("0", "3", false)]
    public void TheTanMediumIsOnlyRequiredWhenBothConditionsHold(string required, string count, bool expected)
    {
        var merged = HitansFixture.Merge(HitansFixture.Segment(6, HitansFixture.Method(6, "942", "pushTAN", mediumRequired: required, activeMedia: count)));

        Assert.Equal(expected, Assert.Single(merged.TanMethods).NeedsTanMedium);
    }

    /// <summary>
    /// Eine Elementversion, deren Feldfolge hier nicht belegt ist, wird NICHT aufgeteilt. Lieber kein
    /// Verfahren als ein erfundenes - genau daran ist die alte Auslegung gescheitert.
    /// </summary>
    [Fact]
    public void AnUnknownElementVersionYieldsNoInventedMethod()
    {
        var merged = HitansFixture.Merge(HitansFixture.Segment(1, HitansFixture.Method(6, "942", "pushTAN")));

        Assert.Empty(merged.TanMethods);
        // Die Version selbst bleibt erhalten - nach ihr entscheidet sich, ob HKTAN mitgeschickt wird.
        Assert.Equal(1, merged.SegmentVersions["HITANS"]);
    }

    /// <summary>
    /// Der eigentliche Grund fuer "9010@5 HKTAN Der gewuenschte Geschaeftsvorfall wird nicht
    /// unterstuetzt": die Bank kuendigt HKTAN nur in einer aelteren Version an. Die starke
    /// Kundenauthentifizierung bei der Dialoginitialisierung gibt es laut Spezifikation erst ab #6 -
    /// vorher wurde trotzdem immer eines mitgeschickt.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    public async Task HkTanTravelsOnlyWhenTheBankAnnouncesItFromVersionSixOn(int announced, bool expected)
    {
        var transport = new CapturingTransport();
        var parameters = HitansFixture.Merge(HitansFixture.Segment(announced, HitansFixture.Method(Math.Max(announced, 6), "942", "pushTAN")))
            with { SystemId = "1234", SecurityFunction = "942" };

        await new FinTsClient(transport).OpenAsync(Bank, Credentials, parameters);

        var sent = transport.Sent();
        Assert.Equal(expected, sent.Find("HKTAN") is not null);
        // Die Anmeldung selbst geht immer hinaus - sie haengt nicht am TAN-Verfahren.
        Assert.NotNull(sent.Find("HKIDN"));
    }

}
