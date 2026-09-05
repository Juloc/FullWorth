using System.Text;
using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

public sealed class FinTsWireTests
{
    [Fact]
    public void Wire_RoundTripsEscapedTextAndBinaryWithoutPhantomElements()
    {
        var binary = Encoding.Latin1.GetBytes("HNSHK:2:4+PIN:1'");
        var source = new FinTsSegment([
            FinTsGroup.Of(FinTsValue.T("HNVSD"), FinTsValue.T("999"), FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.B(binary)),
            FinTsGroup.Of(FinTsValue.T("a+b:c?'@"))
        ]);

        var parsed = Assert.Single(FinTsWire.Parse(FinTsWire.SerializeSegment(source)));
        Assert.Equal("HNVSD", parsed.Type);
        Assert.Equal(binary, parsed.GetBinary(1, 0));
        Assert.Single(parsed.Groups[1].Values);
        Assert.Equal("a+b:c?'@", parsed.GetText(2, 0));
    }

    [Fact]
    public void MessageBuilder_ProducesSizedFinTs30Envelope()
    {
        var parameters = new FinTsBankParameters(0, 0, "0", "999", null,
            new Dictionary<string,int>(), new Dictionary<string,bool>(), [], []);
        var credentials = new FinTsCredentials("1234567890", "secret", "FullWorth-Test");
        var session = new FinTsSessionState("0", 1, parameters);
        var bytes = FinTsMessages.Build(KnownBanks.Ing, credentials, session,
            [FinTsMessages.Identify(KnownBanks.Ing, credentials.UserId, "0"), FinTsMessages.ProcessPrep(parameters, credentials.ProductId), FinTsMessages.Sync()]);

        var segments = FinTsWire.Parse(bytes);
        Assert.Equal("HNHBK", segments[0].Type);
        Assert.Equal(bytes.Length.ToString("D12"), segments[0].GetText(1, 0));
        Assert.Equal("HNVSK", segments[1].Type);
        Assert.Equal("HNVSD", segments[2].Type);
        Assert.NotNull(segments[2].GetBinary(1, 0));
        Assert.Equal("HNHBS", segments[^1].Type);
    }

    [Fact]
    public void IngProfile_IsReadOnlyDataProfile()
    {
        var ing = KnownBanks.Ing;
        Assert.Equal("https://fints.ing.de/fints/", ing.Endpoint.ToString());
        Assert.Contains(FinTsCapability.Accounts, ing.Capabilities);
        Assert.Contains(FinTsCapability.Transactions, ing.Capabilities);
        Assert.Contains(FinTsCapability.Portfolio, ing.Capabilities);
    }
}
