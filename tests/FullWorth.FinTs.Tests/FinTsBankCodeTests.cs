using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Was die Bank meldet, kommt vollstaendig an (#130 §9).
///
/// Bei ING lautete die Meldung im Protokoll regelmaessig nur "9800 Der Dialog wurde abgebrochen".
/// Das ist die Sammelmeldung: sie sagt, DASS der Dialog endete, nicht warum. Der Grund stand in den
/// Codes dahinter - und die wurden weggeworfen, weil die Auswertung den ERSTEN Fehlercode nahm.
/// </summary>
public sealed class FinTsBankCodeTests
{
    [Fact]
    public void AllBankCodesSurviveNotJustTheFirst()
    {
        var response = ResponseWith(
            ("9800", "-", "Der Dialog wurde abgebrochen."),
            ("9942", "3", "PIN falsch."));

        var error = Assert.Throws<FinTsException>(() => response.ThrowOnError());

        Assert.Equal(2, error.BankCodes.Count);
        Assert.Contains(error.BankCodes, code => code.Code == "9800");
        Assert.Contains(error.BankCodes, code => code.Code == "9942");
        Assert.Contains("9800", error.BankCodeSummary);
        Assert.Contains("9942", error.BankCodeSummary);
    }

    [Fact]
    public void TheTellingCodeDecidesTheClassificationNotThePosition()
    {
        // 9800 steht vorn und sagt nichts. Ohne diese Regel hiesse der Fehler "bank_error", und der
        // Benutzer laese "Der Dialog wurde abgebrochen" statt "PIN falsch".
        var response = ResponseWith(
            ("9800", "-", "Der Dialog wurde abgebrochen."),
            ("9942", "3", "PIN falsch."));

        var error = Assert.Throws<FinTsException>(() => response.ThrowOnError());

        Assert.Equal("pin_wrong", error.Code);
        Assert.Equal("9942", error.BankCode);
        Assert.Equal("PIN falsch.", error.BankMessage);
    }

    [Theory]
    [InlineData("9930", "access_locked")]
    [InlineData("9931", "access_locked")]
    [InlineData("9340", "pin_wrong")]
    public void EachKnownCodeGetsItsOwnKind(string code, string expected)
    {
        var response = ResponseWith(("9800", "-", "Der Dialog wurde abgebrochen."), (code, "3", "Meldung."));

        var error = Assert.Throws<FinTsException>(() => response.ThrowOnError());

        Assert.Equal(expected, error.Code);
    }

    /// <summary>
    /// Ein Code, dessen Bedeutung nicht belegt ist, wird NICHT eingeordnet - er bleibt "bank_error".
    /// Eine falsche Einordnung waere schlimmer als keine: sie zeigt dem Benutzer eine Ursache, die
    /// nicht stimmt. Sichtbar ist der Code trotzdem, weil alle Codes mitgefuehrt werden.
    /// </summary>
    [Fact]
    public void ACodeWhoseMeaningIsNotEstablishedIsNotGuessed()
    {
        var response = ResponseWith(("9800", "-", "Der Dialog wurde abgebrochen."), ("9130", "3", "Meldung."));

        var error = Assert.Throws<FinTsException>(() => response.ThrowOnError());

        Assert.Equal("bank_error", error.Code);
        Assert.Contains("9130", error.BankCodeSummary);
    }

    [Fact]
    public void AnUnknownCodeStaysAPlainBankErrorInsteadOfBeingInvented()
    {
        var response = ResponseWith(("9800", "-", "Der Dialog wurde abgebrochen."));

        var error = Assert.Throws<FinTsException>(() => response.ThrowOnError());

        Assert.Equal("bank_error", error.Code);
        Assert.Equal("9800", error.BankCode);
        // Auch allein wird die Meldung mitgefuehrt - sie ist alles, was die Bank gesagt hat.
        Assert.Single(error.BankCodes);
    }

    [Fact]
    public void ASuccessfulResponseThrowsNothing()
    {
        var response = ResponseWith(("0020", "-", "Auftrag ausgefuehrt."));

        response.ThrowOnError();
    }

    /// <summary>
    /// Der Bauplan der gesendeten Nachricht haengt am Fehler - und er enthaelt NUR Struktur.
    ///
    /// Das ist die Antwort auf 9110 "Unbekannter Aufbau der Kundennachricht": ohne den Bauplan bleibt
    /// nur Raten, und die Nachricht selbst darf nicht ins Protokoll - sie traegt im PIN/TAN-Verfahren
    /// die PIN im Signaturblock.
    /// </summary>
    [Fact]
    public void TheShapeOfTheSentMessageTravelsWithTheErrorAndCarriesNoValues()
    {
        var response = ResponseWith(("9110", "-", "Unbekannter Aufbau der Kundennachricht."));
        var shape = new[]
        {
            new FinTsSegmentShape("HKIDN", 2, 4),
            new FinTsSegmentShape("HKTAN", 7, 8)
        };

        var error = Assert.Throws<FinTsException>(() => response.ThrowOnError(shape));

        Assert.Equal("HKIDN:v2:4, HKTAN:v7:8", error.SentShapeSummary);
        // Struktur, keine Werte: weder Benutzername noch PIN noch TAN koennen hier auftauchen.
        Assert.DoesNotContain("=", error.SentShapeSummary);
    }

    private static FinTsResponse ResponseWith(params (string Code, string Reference, string Text)[] codes)
    {
        var groups = new List<FinTsGroup> { FinTsGroup.Of(FinTsValue.T("HIRMS"), FinTsValue.T("3"), FinTsValue.T("2")) };
        foreach (var (code, reference, text) in codes)
            groups.Add(FinTsGroup.Of(FinTsValue.T(code), FinTsValue.T(reference), FinTsValue.T(text)));
        return FinTsResponseParser.Parse(FinTsWire.Serialize([new FinTsSegment(groups)]));
    }
}
