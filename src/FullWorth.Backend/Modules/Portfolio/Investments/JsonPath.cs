using System.Text.Json;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>
/// Liest einen Punktpfad mit optionalen <c>[n]</c>-Indizes aus einem <see cref="JsonElement"/>, z.B.
/// <c>chart.result[0].indicators.adjclose[0].adjclose</c>.
///
/// Wirft absichtlich nie: die Antwort kommt von einer fremden, nicht selbst kontrollierten API, und ein
/// fehlendes oder anders geformtes Segment ist hier "kein Wert gefunden", kein Programmfehler. Der
/// Aufrufer (<see cref="ConfigurableSecurityMarketDataProvider"/>) macht daraus eine leere Liste statt
/// einer Ausnahme.
/// </summary>
internal static class JsonPath
{
    public static JsonElement? Read(JsonElement root, string path)
    {
        var segments = ParseSegments(path);
        if (segments is null) return null;

        var current = root;
        foreach (var segment in segments)
        {
            if (segment.Index is int index)
            {
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                    return null;
                current = current[index];
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment.Name!, out var next))
                    return null;
                current = next;
            }
        }
        return current;
    }

    /// <summary>
    /// Fuer die beiden nebeneinander liegenden Reihen (Datum/Wert), die praktisch jede Kurs-Schnittstelle
    /// liefert - leer statt null, wenn der Pfad fehlt oder kein Array ist.
    /// </summary>
    public static IReadOnlyList<JsonElement> ReadArray(JsonElement root, string path)
    {
        if (Read(root, path) is not { ValueKind: JsonValueKind.Array } array) return [];
        var items = new List<JsonElement>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray()) items.Add(item);
        return items;
    }

    private readonly record struct Segment(string? Name, int? Index);

    /// <summary>
    /// Zerlegt z.B. "a.b[0].c" in [Name a][Name b][Index 0][Name c]. Gibt null zurueck, wenn der Pfad
    /// selbst fehlerhaft ist (unbalancierte Klammer, keine Zahl darin) - ein anderer Grund als "im
    /// Dokument nicht gefunden", aber mit demselben Ergebnis fuer den Aufrufer: kein Wert, kein Absturz.
    /// </summary>
    private static List<Segment>? ParseSegments(string path)
    {
        var segments = new List<Segment>();
        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = rawSegment.IndexOf('[');
            if (bracket < 0)
            {
                segments.Add(new Segment(rawSegment, null));
                continue;
            }
            if (bracket > 0) segments.Add(new Segment(rawSegment[..bracket], null));

            var rest = rawSegment[bracket..];
            while (rest.Length > 0)
            {
                if (rest[0] != '[') return null;
                var close = rest.IndexOf(']');
                if (close < 0) return null;
                if (!int.TryParse(rest[1..close], out var index)) return null;
                segments.Add(new Segment(null, index));
                rest = rest[(close + 1)..];
            }
        }
        return segments;
    }
}
