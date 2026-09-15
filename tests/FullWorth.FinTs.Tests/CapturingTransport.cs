using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Haelt fest, was gesendet wurde, und antwortet mit einem Dialog, der oeffnet.
///
/// Damit laesst sich pruefen, was wirklich auf die Leitung geht - die Nachricht ist im
/// PIN/TAN-Verfahren nicht verschluesselt, sondern steht als Binaerfeld in HNVSD, und
/// <see cref="FinTsResponseParser.Parse"/> packt sie wieder aus.
/// </summary>
internal sealed class CapturingTransport : IFinTsTransport
{
    public List<byte[]> Messages { get; } = [];

    /// <summary>Die gesendete Nachricht, Segment fuer Segment - Umschlag, Signaturblock, Auftraege.</summary>
    public FinTsResponse Sent(int index = 0) => FinTsResponseParser.Parse(Messages[index]);

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
