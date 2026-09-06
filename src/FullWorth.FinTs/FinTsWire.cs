using System.Globalization;
using System.Text;

namespace FullWorth.FinTs;

public abstract record FinTsValue
{
    public sealed record Text(string Value) : FinTsValue;
    public sealed record Binary(byte[] Value) : FinTsValue;
    public sealed record Empty : FinTsValue;

    public static FinTsValue T(string? value) => value is null ? new Empty() : new Text(value);
    public static FinTsValue B(byte[] value) => new Binary(value);
    public static FinTsValue E() => new Empty();
}

public sealed record FinTsGroup(IReadOnlyList<FinTsValue> Values)
{
    public static FinTsGroup Of(params FinTsValue[] values) => new(values);
}

public sealed record FinTsSegment(IReadOnlyList<FinTsGroup> Groups)
{
    public string Type => GetText(0, 0);
    public int Version => int.TryParse(GetText(0, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    public string GetText(int group, int value)
        => Groups.Count > group && Groups[group].Values.Count > value && Groups[group].Values[value] is FinTsValue.Text t ? t.Value : string.Empty;
    public byte[]? GetBinary(int group, int value)
        => Groups.Count > group && Groups[group].Values.Count > value && Groups[group].Values[value] is FinTsValue.Binary b ? b.Value : null;
}

public static class FinTsWire
{
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly HashSet<byte> Reserved = [(byte)'?', (byte)'+', (byte)':', (byte)'\'', (byte)'@'];

    public static byte[] SerializeSegment(FinTsSegment segment)
    {
        using var stream = new MemoryStream();
        for (var g = 0; g < segment.Groups.Count; g++)
        {
            if (g > 0) stream.WriteByte((byte)'+');
            var values = segment.Groups[g].Values;
            var last = values.Count - 1;
            while (last >= 0 && values[last] is FinTsValue.Empty) last--;
            for (var i = 0; i <= last; i++)
            {
                if (i > 0) stream.WriteByte((byte)':');
                WriteValue(stream, values[i]);
            }
        }
        stream.WriteByte((byte)'\'');
        return stream.ToArray();
    }

    public static byte[] Serialize(IEnumerable<FinTsSegment> segments)
    {
        using var stream = new MemoryStream();
        foreach (var segment in segments) stream.Write(SerializeSegment(segment));
        return stream.ToArray();
    }

    public static IReadOnlyList<FinTsSegment> Parse(byte[] bytes)
    {
        var segments = new List<FinTsSegment>();
        var groups = new List<FinTsGroup>();
        var values = new List<FinTsValue>();
        var text = new List<byte>();
        var valueComplete = false;
        var i = 0;

        void FinishValue(bool forceEmpty)
        {
            if (text.Count > 0)
            {
                values.Add(FinTsValue.T(Latin1.GetString([.. text])));
                text.Clear();
                valueComplete = true;
                return;
            }
            if (forceEmpty && !valueComplete)
            {
                values.Add(FinTsValue.E());
                valueComplete = true;
            }
        }

        void FinishGroup()
        {
            FinishValue(values.Count == 0);
            groups.Add(new FinTsGroup(values.ToArray()));
            values = new List<FinTsValue>();
            valueComplete = false;
        }

        void FinishSegment()
        {
            FinishGroup();
            segments.Add(new FinTsSegment(groups.ToArray()));
            groups = new List<FinTsGroup>();
        }

        while (i < bytes.Length)
        {
            var b = bytes[i];
            if (b == (byte)'?')
            {
                if (++i >= bytes.Length) throw new FinTsException("Dangling FinTS escape character.", "wire_escape");
                text.Add(bytes[i++]);
                valueComplete = false;
                continue;
            }
            if (b == (byte)'@')
            {
                if (text.Count > 0) FinishValue(false);
                i++;
                var lengthDigits = new List<byte>();
                while (i < bytes.Length && bytes[i] != (byte)'@') lengthDigits.Add(bytes[i++]);
                if (i >= bytes.Length || !int.TryParse(Latin1.GetString([.. lengthDigits]), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
                    throw new FinTsException("Malformed FinTS binary length.", "wire_binary");
                i++;
                if (i + length > bytes.Length) throw new FinTsException("Truncated FinTS binary value.", "wire_binary");
                values.Add(FinTsValue.B(bytes.AsSpan(i, length).ToArray()));
                valueComplete = true;
                i += length;
                continue;
            }
            if (b == (byte)':')
            {
                FinishValue(true);
                valueComplete = false;
                i++;
                continue;
            }
            if (b == (byte)'+')
            {
                FinishGroup();
                i++;
                continue;
            }
            if (b == (byte)'\'')
            {
                FinishSegment();
                i++;
                continue;
            }
            text.Add(b);
            valueComplete = false;
            i++;
        }
        if (text.Count > 0 || values.Count > 0 || groups.Count > 0)
            throw new FinTsException("FinTS message ended without segment terminator.", "wire_segment");
        return segments;
    }

    private static void WriteValue(Stream stream, FinTsValue value)
    {
        switch (value)
        {
            case FinTsValue.Empty:
                return;
            case FinTsValue.Binary binary:
                var prefix = Latin1.GetBytes($"@{binary.Value.Length}@");
                stream.Write(prefix);
                stream.Write(binary.Value);
                return;
            case FinTsValue.Text text:
                foreach (var b in Latin1.GetBytes(text.Value))
                {
                    if (Reserved.Contains(b)) stream.WriteByte((byte)'?');
                    stream.WriteByte(b);
                }
                return;
        }
    }
}
