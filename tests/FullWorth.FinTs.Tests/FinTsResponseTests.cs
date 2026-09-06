using System.Text;
using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

public sealed class FinTsResponseTests
{
    [Fact]
    public void ResponseParser_UnwrapsHnvsdAndReadsBalance()
    {
        var inner = FinTsWire.Serialize([
            new FinTsSegment([
                FinTsGroup.Of(FinTsValue.T("HISAL"), FinTsValue.T("3"), FinTsValue.T("7")),
                FinTsGroup.Of(FinTsValue.T("DE02120300000000202051"), FinTsValue.T("INGDDEFFXXX")),
                FinTsGroup.Of(FinTsValue.T("Girokonto")),
                FinTsGroup.Of(FinTsValue.T("EUR")),
                FinTsGroup.Of(FinTsValue.T("C"), FinTsValue.T("1234,56"), FinTsValue.T("EUR"), FinTsValue.T("20260906"))
            ])
        ]);
        var responseBytes = FinTsWire.Serialize([
            new FinTsSegment([
                FinTsGroup.Of(FinTsValue.T("HNHBK"), FinTsValue.T("1"), FinTsValue.T("3")),
                FinTsGroup.Of(FinTsValue.T("000000000000")), FinTsGroup.Of(FinTsValue.T("300")), FinTsGroup.Of(FinTsValue.T("D1")), FinTsGroup.Of(FinTsValue.T("1"))
            ]),
            new FinTsSegment([FinTsGroup.Of(FinTsValue.T("HNVSD"), FinTsValue.T("999"), FinTsValue.T("1")), FinTsGroup.Of(FinTsValue.B(inner))])
        ]);

        var response = FinTsResponseParser.Parse(responseBytes);
        var balance = FinTsResponseParser.Balance(response);
        Assert.NotNull(balance);
        Assert.Equal(1234.56m, balance!.Amount);
        Assert.Equal("EUR", balance.Currency);
    }

    [Fact]
    public void ResponseParser_ParsesMt940Bookings()
    {
        var mt940 = Encoding.Latin1.GetBytes(":60F:C260901EUR0,00\n:61:2609050905D12,34NTRFNONREF\n:86:?00KARTENZAHLUNG?32SUPERMARKT\n:62F:C260905EUR0,00");
        var inner = FinTsWire.Serialize([
            new FinTsSegment([
                FinTsGroup.Of(FinTsValue.T("HIKAZ"), FinTsValue.T("3"), FinTsValue.T("7")),
                FinTsGroup.Of(FinTsValue.B(mt940))
            ])
        ]);
        var responseBytes = FinTsWire.Serialize([
            new FinTsSegment([FinTsGroup.Of(FinTsValue.T("HNVSD"), FinTsValue.T("999"), FinTsValue.T("1")), FinTsGroup.Of(FinTsValue.B(inner))])
        ]);
        var tx = Assert.Single(FinTsResponseParser.Transactions(FinTsResponseParser.Parse(responseBytes)));
        Assert.Equal(-12.34m, tx.Amount);
        Assert.Equal("SUPERMARKT", tx.Counterparty);
    }
}
