using System.Globalization;

namespace FullWorth.FinTs;

internal static class FinTsMessages
{
    private static FinTsGroup Header(string type, int number, int version, int? reference = null)
        => FinTsGroup.Of(
            FinTsValue.T(type),
            FinTsValue.T(number.ToString(CultureInfo.InvariantCulture)),
            FinTsValue.T(version.ToString(CultureInfo.InvariantCulture)),
            reference.HasValue ? FinTsValue.T(reference.Value.ToString(CultureInfo.InvariantCulture)) : FinTsValue.E());

    internal static byte[] Build(
        FinTsBankProfile bank,
        FinTsCredentials credentials,
        FinTsSessionState session,
        IEnumerable<FinTsSegment> businessSegments,
        string? tan = null)
    {
        var securityReference = Random.Shared.Next(1_000_000, 9_999_999);
        var number = 2;
        var inner = new List<FinTsSegment>
        {
            SignatureHeader(number++, session.Parameters.SecurityFunction, securityReference, bank.Blz, credentials.UserId, session.Parameters.SystemId)
        };
        foreach (var source in businessSegments)
        {
            var groups = source.Groups.ToArray();
            var first = groups[0].Values.ToArray();
            first[1] = FinTsValue.T((number++).ToString(CultureInfo.InvariantCulture));
            groups[0] = new FinTsGroup(first);
            inner.Add(new FinTsSegment(groups));
        }
        inner.Add(SignatureFooter(number++, securityReference, credentials.Pin, tan));
        var innerBytes = FinTsWire.Serialize(inner);

        var outer = new List<FinTsSegment>
        {
            EncryptionHeader(bank.Blz, credentials.UserId, session.Parameters.SystemId),
            new([Header("HNVSD", 999, 1), FinTsGroup.Of(FinTsValue.B(innerBytes))]),
            new([Header("HNHBS", number, 1), FinTsGroup.Of(FinTsValue.T(session.MessageNumber.ToString(CultureInfo.InvariantCulture)))])
        };
        var body = FinTsWire.Serialize(outer);
        var placeholder = MessageHeader(0, session.DialogId, session.MessageNumber);
        var placeholderBytes = FinTsWire.SerializeSegment(placeholder);
        var totalSize = placeholderBytes.Length + body.Length;
        var finalHeader = FinTsWire.SerializeSegment(MessageHeader(totalSize, session.DialogId, session.MessageNumber));
        using var stream = new MemoryStream(finalHeader.Length + body.Length);
        stream.Write(finalHeader);
        stream.Write(body);
        return stream.ToArray();
    }

    internal static FinTsSegment Identify(FinTsBankProfile bank, string userId, string systemId)
        => new([
            Header("HKIDN", 0, 2),
            FinTsGroup.Of(FinTsValue.T("280"), FinTsValue.T(bank.Blz)),
            FinTsGroup.Of(FinTsValue.T(userId)),
            FinTsGroup.Of(FinTsValue.T(systemId)),
            FinTsGroup.Of(FinTsValue.T("1"))
        ]);

    internal static FinTsSegment ProcessPrep(FinTsBankParameters parameters, string productId)
        => new([
            Header("HKVVB", 0, 3),
            FinTsGroup.Of(FinTsValue.T(parameters.BpdVersion.ToString(CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.T(parameters.UpdVersion.ToString(CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T(productId)),
            FinTsGroup.Of(FinTsValue.T("1.0"))
        ]);

    internal static FinTsSegment Sync()
        => new([Header("HKSYN", 0, 3), FinTsGroup.Of(FinTsValue.T("0"))]);

    internal static FinTsSegment End(string dialogId)
        => new([Header("HKEND", 0, 1), FinTsGroup.Of(FinTsValue.T(dialogId))]);

    internal static FinTsSegment Balance(FinTsAccount account, int version, string? touchdown = null)
    {
        var groups = new List<FinTsGroup> { Header("HKSAL", 0, version), AccountGroup(account, version), FinTsGroup.Of(FinTsValue.T("N")), FinTsGroup.Of(FinTsValue.E()) };
        if (!string.IsNullOrWhiteSpace(touchdown)) groups.Add(FinTsGroup.Of(FinTsValue.T(touchdown)));
        return new(groups);
    }

    internal static FinTsSegment Transactions(FinTsAccount account, int version, DateOnly from, DateOnly to, string? touchdown = null)
    {
        var groups = new List<FinTsGroup>
        {
            Header("HKKAZ", 0, version), AccountGroup(account, version), FinTsGroup.Of(FinTsValue.T("N")),
            FinTsGroup.Of(FinTsValue.T(from.ToString("yyyyMMdd", CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.T(to.ToString("yyyyMMdd", CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.E())
        };
        if (!string.IsNullOrWhiteSpace(touchdown)) groups.Add(FinTsGroup.Of(FinTsValue.T(touchdown)));
        return new(groups);
    }

    internal static FinTsSegment Portfolio(FinTsAccount account, int version, string? currency = null, string? touchdown = null)
    {
        var groups = new List<FinTsGroup>
        {
            Header("HKWPD", 0, version), AccountGroup(account, version),
            FinTsGroup.Of(string.IsNullOrWhiteSpace(currency) ? FinTsValue.E() : FinTsValue.T(currency)),
            FinTsGroup.Of(FinTsValue.E()), FinTsGroup.Of(FinTsValue.E())
        };
        if (!string.IsNullOrWhiteSpace(touchdown)) groups.Add(FinTsGroup.Of(FinTsValue.T(touchdown)));
        return new(groups);
    }

    internal static FinTsSegment TanProcess4(string referencedSegment, int version, string? medium)
    {
        var groups = new List<FinTsGroup>
        {
            Header("HKTAN", 0, version), FinTsGroup.Of(FinTsValue.T("4")), FinTsGroup.Of(FinTsValue.T(referencedSegment))
        };
        if (version >= 6)
        {
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
        }
        if (!string.IsNullOrWhiteSpace(medium))
        {
            if (version >= 6)
            {
                groups.Add(FinTsGroup.Of(FinTsValue.E()));
                groups.Add(FinTsGroup.Of(FinTsValue.E()));
                groups.Add(FinTsGroup.Of(FinTsValue.E()));
            }
            groups.Add(FinTsGroup.Of(FinTsValue.T(medium)));
        }
        return new(groups);
    }

    internal static FinTsSegment TanProcess2(string taskReference, int version, string? medium)
        => TanContinuation("2", taskReference, version, medium);

    internal static FinTsSegment TanPoll(string taskReference, int version, string? medium)
        => TanContinuation("S", taskReference, version, medium);

    private static FinTsSegment TanContinuation(string process, string taskReference, int version, string? medium)
    {
        var groups = new List<FinTsGroup> { Header("HKTAN", 0, version), FinTsGroup.Of(FinTsValue.T(process)) };
        if (version >= 6)
        {
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
            groups.Add(FinTsGroup.Of(FinTsValue.T(taskReference)));
            groups.Add(FinTsGroup.Of(FinTsValue.T("N")));
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
        }
        else
        {
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
            groups.Add(FinTsGroup.Of(FinTsValue.E()));
            groups.Add(FinTsGroup.Of(FinTsValue.T(taskReference)));
            groups.Add(FinTsGroup.Of(FinTsValue.T("N")));
        }
        if (!string.IsNullOrWhiteSpace(medium))
        {
            if (version >= 6)
            {
                groups.Add(FinTsGroup.Of(FinTsValue.E()));
                groups.Add(FinTsGroup.Of(FinTsValue.E()));
                groups.Add(FinTsGroup.Of(FinTsValue.E()));
            }
            groups.Add(FinTsGroup.Of(FinTsValue.T(medium)));
        }
        return new(groups);
    }

    private static FinTsSegment MessageHeader(int size, string dialogId, int messageNumber)
        => new([
            Header("HNHBK", 1, 3),
            FinTsGroup.Of(FinTsValue.T(size.ToString("D12", CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.T("300")),
            FinTsGroup.Of(FinTsValue.T(dialogId)),
            FinTsGroup.Of(FinTsValue.T(messageNumber.ToString(CultureInfo.InvariantCulture)))
        ]);

    private static FinTsSegment EncryptionHeader(string blz, string userId, string systemId)
    {
        var now = DateTime.Now;
        return new([
            Header("HNVSK", 998, 3),
            FinTsGroup.Of(FinTsValue.T("PIN"), FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("998")),
            FinTsGroup.Of(FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("2"), FinTsValue.E(), FinTsValue.T(systemId)),
            FinTsGroup.Of(FinTsValue.T("1"), FinTsValue.T(now.ToString("yyyyMMdd")), FinTsValue.T(now.ToString("HHmmss"))),
            FinTsGroup.Of(FinTsValue.T("2"), FinTsValue.T("2"), FinTsValue.T("13"), FinTsValue.B(new byte[8]), FinTsValue.T("5"), FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("280"), FinTsValue.T(blz), FinTsValue.T(userId), FinTsValue.T("V"), FinTsValue.T("0"), FinTsValue.T("0")),
            FinTsGroup.Of(FinTsValue.T("0"))
        ]);
    }

    private static FinTsSegment SignatureHeader(int number, string securityFunction, int reference, string blz, string userId, string systemId)
    {
        var now = DateTime.Now;
        return new([
            Header("HNSHK", number, 4),
            FinTsGroup.Of(FinTsValue.T("PIN"), FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T(securityFunction)),
            FinTsGroup.Of(FinTsValue.T(reference.ToString(CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.T("1")), FinTsGroup.Of(FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("2"), FinTsValue.E(), FinTsValue.T(systemId)),
            FinTsGroup.Of(FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("1"), FinTsValue.T(now.ToString("yyyyMMdd")), FinTsValue.T(now.ToString("HHmmss"))),
            FinTsGroup.Of(FinTsValue.T("1"), FinTsValue.T("999"), FinTsValue.T("1")),
            FinTsGroup.Of(FinTsValue.T("6"), FinTsValue.T("10"), FinTsValue.T("16")),
            FinTsGroup.Of(FinTsValue.T("280"), FinTsValue.T(blz), FinTsValue.T(userId), FinTsValue.T("S"), FinTsValue.T("0"), FinTsValue.T("0"))
        ]);
    }

    private static FinTsSegment SignatureFooter(int number, int reference, string pin, string? tan)
    {
        var signature = new List<FinTsValue> { FinTsValue.T(pin) };
        if (tan is not null) signature.Add(new FinTsValue.Text(tan));
        return new([
            Header("HNSHA", number, 2),
            FinTsGroup.Of(FinTsValue.T(reference.ToString(CultureInfo.InvariantCulture))),
            FinTsGroup.Of(FinTsValue.E()),
            new FinTsGroup(signature)
        ]);
    }

    private static FinTsGroup AccountGroup(FinTsAccount account, int version)
        => version >= 6
            ? FinTsGroup.Of(FinTsValue.T(account.Iban), FinTsValue.T(account.Bic))
            : FinTsGroup.Of(FinTsValue.T(account.AccountNumber ?? account.Iban), FinTsValue.T(account.SubAccount), FinTsValue.T("280"), FinTsValue.T(account.Bic));
}
