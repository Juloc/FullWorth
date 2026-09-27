using System.Security.Cryptography;
using System.Text;
using FullWorth.FinTs;
using FullWorth.Shared;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Das Feld :86: so, wie die Bank es liefert - harte Umbrueche nach 65 Zeichen mitten im Wort.
/// Die Beispiele sind erfunden, der Aufbau ist der echte.
/// </summary>
public sealed class Mt940InformationTests
{
    private const string Gutschrift =
        "166?00Gutschrift?100062?20SVWZ+Brille Anteil Sept?21ember?30COBADEFFXXX?31DE89 3704 0044 05\n" +
        "32 0130 00?32Erika Musterfr?33au";

    [Fact]
    public void Der_Zweck_steht_ohne_die_Kennungen_da_und_die_Stuecke_ohne_Trenner()
    {
        var info = Mt940Information.Parse(Gutschrift);

        Assert.Equal("Gutschrift", info.PostingText);
        Assert.Equal("Brille Anteil September", info.Purpose);
    }

    [Fact]
    public void Ein_Zeilenumbruch_ist_kein_Leerzeichen()
    {
        var info = Mt940Information.Parse(Gutschrift);

        Assert.Equal("Erika Musterfrau", info.CounterpartyName);
        Assert.Equal("DE89370400440532013000", info.CounterpartyAccount);
        Assert.Equal("COBADEFFXXX", info.CounterpartyBankCode);
    }

    [Fact]
    public void Die_SEPA_Kennungen_werden_getrennt_und_NOTPROVIDED_ist_keine_Referenz()
    {
        var info = Mt940Information.Parse(
            "105?00Lastschrifteinzug?20EREF+NOTPROVIDED?21MREF+M-4711?22CRED+DE98ZZZ09999999999?23SVWZ+Strom August");

        Assert.Equal("Strom August", info.Purpose);
        Assert.Null(info.EndToEndReference);
        Assert.Equal("M-4711", info.MandateReference);
        Assert.Equal("DE98ZZZ09999999999", info.CreditorId);
    }

    [Fact]
    public void Ohne_Zweck_bleibt_der_Buchungstext_und_ohne_Unterfelder_ist_alles_Zweck()
    {
        Assert.Null(Mt940Information.Parse("005?00Abschluss?10000").Purpose);
        Assert.Equal("Abschluss", Mt940Information.Parse("005?00Abschluss?10000").PostingText);
        Assert.Equal("Miete Oktober", Mt940Information.Parse("Miete   Oktober").Purpose);
        Assert.Null(Mt940Information.Parse("  ").Purpose);
    }

    [Fact]
    public void Der_Abruf_liefert_lesbaren_Text_Gegenseite_und_Gegenkonto()
    {
        var tx = Assert.Single(Parse(Mt940($":86:{Gutschrift}")));

        Assert.Equal("Brille Anteil September", tx.Description);
        Assert.Equal("Erika Musterfrau", tx.Counterparty);
        Assert.Equal("DE89370400440532013000", tx.CounterpartyAccount);
    }

    [Fact]
    public void Der_Schluessel_einer_Buchung_bleibt_der_alte()
    {
        // Ein neuer Schluessel hiesse: jede schon gespeicherte Buchung stuende nach dem naechsten Abruf zweimal da.
        var tx = Assert.Single(Parse(Mt940($":86:{Gutschrift}")));

        var legacyDesc = Gutschrift.Replace("\n", " ").Trim();
        var material = $"20260905|20260905|{200.00m}|EUR|NTRFNONREF|{legacyDesc}|False";
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))), tx.ExternalKey);
    }

    private static string Mt940(string field86)
        => ":20:STARTUMS\r\n:25:10020030/1234567890\r\n:60F:C260901EUR0,00\r\n" +
           ":61:2609050905C200,00NTRFNONREF\r\n" + field86.Replace("\n", "\r\n") + "\r\n:62F:C260905EUR200,00";

    private static IReadOnlyList<FinTsTransaction> Parse(string mt940)
    {
        var inner = FinTsWire.Serialize([
            new FinTsSegment([
                FinTsGroup.Of(FinTsValue.T("HIKAZ"), FinTsValue.T("3"), FinTsValue.T("7")),
                FinTsGroup.Of(FinTsValue.B(Encoding.Latin1.GetBytes(mt940)))
            ])
        ]);
        var response = FinTsWire.Serialize([
            new FinTsSegment([FinTsGroup.Of(FinTsValue.T("HNVSD"), FinTsValue.T("999"), FinTsValue.T("1")), FinTsGroup.Of(FinTsValue.B(inner))])
        ]);
        return FinTsResponseParser.Transactions(FinTsResponseParser.Parse(response));
    }
}
