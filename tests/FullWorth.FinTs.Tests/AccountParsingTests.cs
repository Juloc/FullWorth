using System.Globalization;
using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Konten kommen aus HIUPD - gelesen an der richtigen Stelle, und ein Depot erkennt man an der
/// Kontoart, nicht am Namen.
///
/// Nach der ersten geglueckten ING-Anmeldung waren Giro- und Extra-Konto da, das Depot nicht. Drei
/// Fehler lagen hintereinander, und jeder allein haette gereicht:
///
/// 1. Die Stellen in HIUPD verschieben sich mit der Segmentversion, weil #6 die IBAN an Stelle 3
///    EINSCHIEBT. Gelesen wurden fest die Stellen von #6; ING schickt #5.
/// 2. Als Depot galt, was "Depot" in der Produktbezeichnung stehen hatte. Das ist geraten. Die
///    Spezifikation hat dafuer ein Feld: Kontoart, 30-39 Wertpapierdepot, 60-69 Fonds-Depot.
/// 3. Ein Depot hat keine IBAN - es wird ueber die Depotnummer angesprochen. Die Synchronisation
///    uebersprang aber jedes Konto ohne IBAN.
/// </summary>
public sealed class AccountParsingTests
{
    /// <summary>
    /// Die Kontoart entscheidet. 1-9 Girokonto, 10-19 Sparkonto, 30-39 Wertpapierdepot,
    /// 60-69 Fonds-Depot (FinTS 3.0 Formals, Data Dictionary "Kontoart").
    /// </summary>
    [Theory]
    [InlineData("1", false)]
    [InlineData("9", false)]
    [InlineData("10", false)]
    [InlineData("30", true)]
    [InlineData("33", true)]
    [InlineData("39", true)]
    [InlineData("40", false)]
    [InlineData("60", true)]
    [InlineData("69", true)]
    [InlineData("70", false)]
    public void TheAccountKindDecidesWhetherItIsADepot(string kind, bool expected)
    {
        var account = Assert.Single(Parse(Hiupd(5, kind: kind, product: "Direkt-Depot")).Accounts);

        Assert.Equal(expected, account.IsDepot);
    }

    /// <summary>
    /// Ein Depot, das die Bank nicht "Depot" nennt, ist trotzdem eines. Genau hier verschwand es.
    /// </summary>
    [Fact]
    public void ADepotUnderAnyNameIsStillADepot()
    {
        var account = Assert.Single(Parse(Hiupd(5, kind: "33", product: "Wertpapieranlage")).Accounts);

        Assert.True(account.IsDepot);
        Assert.Equal("Wertpapieranlage", account.ProductName);
    }

    /// <summary>
    /// Nennt die Bank keine Kontoart - sie ist optional - bleibt nur der Name. Ein Rueckfall, kein
    /// Verfahren.
    /// </summary>
    [Theory]
    [InlineData("Direkt-Depot", true)]
    [InlineData("Girokonto", false)]
    public void WithoutAnAccountKindTheNameIsTheLastResort(string product, bool expected)
    {
        var account = Assert.Single(Parse(Hiupd(5, kind: "", product: product)).Accounts);

        Assert.Equal(expected, account.IsDepot);
    }

    /// <summary>
    /// Die Stellen verschieben sich zwischen #5 und #6. Vorher las die Auslegung bei #5 den zweiten
    /// Kontoinhaber als ersten und das Kontolimit als Produktbezeichnung.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void TheFieldsAreReadWhereTheVersionPutsThem(int version)
    {
        var account = Assert.Single(Parse(Hiupd(version, kind: "1", product: "Girokonto")).Accounts);

        Assert.Equal("Erika Musterfrau", account.Owner);
        Assert.Equal("Girokonto", account.ProductName);
        Assert.Equal("EUR", account.Currency);
        Assert.False(account.IsDepot);
    }

    /// <summary>Ein Depot bringt eine Depotnummer mit, keine IBAN - und bleibt trotzdem erhalten.</summary>
    [Fact]
    public void ADepotSurvivesWithoutAnIban()
    {
        var account = Assert.Single(Parse(Hiupd(5, kind: "33", product: "Direkt-Depot", iban: "")).Accounts);

        Assert.True(account.IsDepot);
        Assert.Equal(string.Empty, account.Iban);
        Assert.Equal("1234567", account.AccountNumber);
    }

    private static FinTsBankParameters Parse(FinTsSegment segment) => HitansFixture.Merge(segment);

    /// <summary>
    /// Ein HIUPD-Segment. Ab #6 schiebt sich die IBAN an Stelle 3 und verschiebt alles dahinter
    /// (FinTS 3.0 Formals, E "Kontoinformation").
    /// </summary>
    private static FinTsSegment Hiupd(int version, string kind, string product, string iban = "DE02500105170137075030")
    {
        var groups = new List<FinTsGroup>
        {
            FinTsGroup.Of(
                FinTsValue.T("HIUPD"),
                FinTsValue.T("15"),
                FinTsValue.T(version.ToString(CultureInfo.InvariantCulture)),
                FinTsValue.T("7")),
            // 2 Kontoverbindung: Kontonummer, Unterkonto, Laenderkennzeichen, Kreditinstitutscode
            FinTsGroup.Of(FinTsValue.T("1234567"), FinTsValue.E(), FinTsValue.T("280"), FinTsValue.T("50010517"))
        };
        if (version >= 6) groups.Add(FinTsGroup.Of(FinTsValue.T(iban)));   // 3 IBAN
        groups.Add(FinTsGroup.Of(FinTsValue.T("kunde1")));                 // Kunden-ID
        groups.Add(FinTsGroup.Of(FinTsValue.T(kind)));                     // Kontoart
        groups.Add(FinTsGroup.Of(FinTsValue.T("EUR")));                    // Kontowaehrung
        groups.Add(FinTsGroup.Of(FinTsValue.T("Erika Musterfrau")));       // Name Kontoinhaber 1
        groups.Add(FinTsGroup.Of(FinTsValue.T("Max Mustermann")));         // Name Kontoinhaber 2
        groups.Add(FinTsGroup.Of(FinTsValue.T(product)));                  // Kontoproduktbezeichnung
        groups.Add(FinTsGroup.Of(FinTsValue.T("E"), FinTsValue.T("1000,")));// Kontolimit
        // Bis #5 gibt es kein IBAN-Feld; sie steht dann nirgends im Segment.
        if (version < 6 && !string.IsNullOrEmpty(iban))
            groups.Add(FinTsGroup.Of(FinTsValue.T("HKSAL"), FinTsValue.T("1"), FinTsValue.T(iban)));
        return new FinTsSegment(groups);
    }
}
