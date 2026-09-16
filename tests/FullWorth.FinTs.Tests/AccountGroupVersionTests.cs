using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Welche Kontoverbindung in einen Auftrag gehoert, haengt an der Segmentversion - und die Grenze lag
/// um genau eine Version zu tief.
///
/// Gemeldet wurde es als "wo ist mein Depot?": vier Konten kamen an, das Depot nicht. Die ING sagte,
/// woran es lag, und zeigte dabei auf die Stelle:
///
/// <code>
/// 9050 Nachricht teilweise fehlerhaft.; 9160@3 HKWPD Ein erforderliches Datenelement fehlt.
/// SentShape=HNHBK#1:v3:4, HNVSK#998:v3:8, HNVSD#999:v1:1, HNSHK#2:v4:11, HKWPD#3:v6:4, ...
/// </code>
///
/// Drei Gruppen, nicht zwei:
///
/// <list type="bullet">
/// <item>bis 5 (Account2) und 6 (Account3): Kontonummer, Unterkontomerkmal, Laenderkennzeichen,
///   Kreditinstitutscode. Alle Pflicht, auf der Leitung identisch, und eine IBAN kommt nicht vor.</item>
/// <item>ab 7 (KTI): IBAN, BIC, Kontonummer, Unterkontomerkmal, Kreditinstitutskennung - alle KANN,
///   deshalb ist "IBAN und BIC und sonst nichts" gueltig.</item>
/// </list>
///
/// Warum es bei Giro und Extra-Konto nicht auffiel: fuer HKSAL und HKKAZ kuendigt die ING Version 7
/// an. Dort ist die abgeschnittene KTI erlaubt. HKWPD gibt es nur bis Version 6 - ein Depot lief
/// also IMMER in die falsche Gruppe.
/// </summary>
public sealed class AccountGroupVersionTests
{
    private static readonly FinTsBankProfile Bank = new(
        "ing", "ING", "50010517", "INGDDEFFXXX", new Uri("https://fints.example/fints"), new HashSet<FinTsCapability>());

    private static readonly FinTsCredentials Credentials = new("user", "pin", "PRODUCT01");

    /// <summary>
    /// Der gemeldete Fall. Version 6 traegt die klassische Kontoverbindung, und ihre vier Angaben
    /// sind alle Pflicht - genau die fehlten.
    /// </summary>
    [Fact]
    public async Task ADepotRequestCarriesTheClassicAccountWithBankCode()
    {
        var hkwpd = await SentPortfolioAsync(6);

        Assert.Equal(6, hkwpd.Version);
        Assert.Equal("9876543210", hkwpd.GetText(1, 0));   // Depotnummer
        Assert.Equal("00", hkwpd.GetText(1, 1));           // Unterkontomerkmal
        Assert.Equal("280", hkwpd.GetText(1, 2));          // Laenderkennzeichen
        Assert.Equal("50010517", hkwpd.GetText(1, 3));     // Kreditinstitutscode
    }

    /// <summary>Und niemals eine IBAN, wo die Kontonummer hingehoert - das war der Fehler.</summary>
    [Fact]
    public async Task AVersionSixRequestNeverPutsAnIbanWhereTheAccountNumberBelongs()
    {
        var hkwpd = await SentPortfolioAsync(6);

        Assert.DoesNotContain("DE", hkwpd.GetText(1, 0));
        Assert.NotEqual("INGDDEFFXXX", hkwpd.GetText(1, 1));
        // Vier Angaben, keine abgeschnittene Gruppe.
        Assert.Equal(4, hkwpd.Groups[1].Values.Count);
    }

    /// <summary>
    /// Ab Version 7 ist es umgekehrt: dort steht die internationale Kontoverbindung, und weil jedes
    /// ihrer Elemente KANN ist, genuegen IBAN und BIC. Das ist der Weg, den Giro und Extra-Konto
    /// heute gehen - er darf sich nicht aendern.
    /// </summary>
    [Fact]
    public async Task ABalanceRequestAtVersionSevenStillCarriesIbanAndBic()
    {
        var hksal = await SentBalanceAsync(7);

        Assert.Equal(7, hksal.Version);
        Assert.Equal("DE02500105170137075030", hksal.GetText(1, 0));
        Assert.Equal("INGDDEFFXXX", hksal.GetText(1, 1));
    }

    /// <summary>Version 6 dagegen auch beim Saldo die klassische Gruppe - dieselbe Regel, ein Segment weiter.</summary>
    [Fact]
    public async Task ABalanceRequestAtVersionSixCarriesTheClassicAccount()
    {
        var hksal = await SentBalanceAsync(6);

        Assert.Equal("1234567890", hksal.GetText(1, 0));
        Assert.Equal("280", hksal.GetText(1, 2));
        Assert.Equal("50010517", hksal.GetText(1, 3));
    }

    /// <summary>
    /// Ein Depot hat keine IBAN. Kuendigt die Bank fuer einen Auftrag Version 7 an, waere das fuer
    /// ein Depot eine Nachricht ohne Konto - deshalb geht er in Version 6 hinaus.
    /// </summary>
    [Fact]
    public async Task AnAccountWithoutAnIbanNeverGoesOutAtVersionSeven()
    {
        var hksal = await SentBalanceAsync(7, Depot);

        Assert.Equal(6, hksal.Version);
        Assert.Equal("9876543210", hksal.GetText(1, 0));
        Assert.Equal("50010517", hksal.GetText(1, 3));
    }

    private static FinTsAccount Cash => new(
        "DE02500105170137075030", "INGDDEFFXXX", "1234567890", "00", "Owner", "Girokonto", "EUR",
        IsDepot: false, BankCode: "50010517");

    private static FinTsAccount Depot => new(
        string.Empty, string.Empty, "9876543210", "00", "Owner", "Direkt-Depot", "EUR",
        IsDepot: true, BankCode: "50010517");

    private static async Task<FinTsSegment> SentPortfolioAsync(int announcedVersion)
    {
        var transport = new CapturingTransport();
        await new FinTsClient(transport).GetPortfolioAsync(
            Bank, Credentials, Session(Parameters("HIWPDS", announcedVersion)), Depot);

        return Require(transport, "HKWPD");
    }

    private static async Task<FinTsSegment> SentBalanceAsync(int announcedVersion, FinTsAccount? account = null)
    {
        var transport = new CapturingTransport();
        await new FinTsClient(transport).GetBalanceAsync(
            Bank, Credentials, Session(Parameters("HISALS", announcedVersion)), account ?? Cash);

        return Require(transport, "HKSAL");
    }

    private static FinTsSegment Require(CapturingTransport transport, string name)
    {
        var segment = Assert.Single(transport.Messages) is not null ? transport.Sent().Find(name) : null;
        Assert.NotNull(segment);
        return segment!;
    }

    private static FinTsSessionState Session(FinTsBankParameters parameters)
        => new("DIALOG01", 2, parameters);

    private static FinTsBankParameters Parameters(string segment, int version) => new(
        0, 0, "1234", "999", null,
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [segment] = version },
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
        [], []);
}
