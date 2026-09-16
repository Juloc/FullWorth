using System.Globalization;
using FullWorth.FinTs;

namespace FullWorth.FinTs.Tests;

/// <summary>
/// Baut ein HITANS-Segment so, wie eine Bank es schickt.
///
/// Die Struktur ist der Kern von #130 §8 und steht deshalb an EINER Stelle: Kopf, drei eigene Stellen,
/// dann der EINE Parameterblock, in dem alle Verfahren hintereinander stehen. Die alten Fixtures legten
/// jedes Verfahren in eine eigene Gruppe - sie waren gegen die kaputte Auslegung geschrieben und haben
/// sie dadurch bestaetigt statt gepruefт.
///
/// Feldfolge nach FinTS 3.0, Security - Sicherheitsverfahren PIN/TAN, Data-Dictionary
/// "Verfahrensparameter Zwei-Schritt-Verfahren": 21 Felder bei Elementversion #6, 26 bei #7.
/// </summary>
internal static class HitansFixture
{
    public static FinTsSegment Segment(int version, params IEnumerable<string>[] methods)
    {
        var block = new List<FinTsValue>
        {
            FinTsValue.T("J"),   // 1 Einschritt-Verfahren erlaubt
            FinTsValue.T("N"),   // 2 mehr als ein TAN-pflichtiger Auftrag pro Nachricht
            FinTsValue.T("0")    // 3 Auftrags-Hashwertverfahren
        };
        foreach (var method in methods)
            block.AddRange(method.Select(FinTsValue.T));

        return new FinTsSegment([
            FinTsGroup.Of(
                FinTsValue.T("HITANS"),
                FinTsValue.T("5"),
                FinTsValue.T(version.ToString(CultureInfo.InvariantCulture)),
                FinTsValue.T("4")),
            FinTsGroup.Of(FinTsValue.T("1")),   // Maximale Anzahl Auftraege
            FinTsGroup.Of(FinTsValue.T("1")),   // Anzahl Signaturen mindestens
            FinTsGroup.Of(FinTsValue.T("0")),   // Sicherheitsklasse
            new FinTsGroup(block)
        ]);
    }

    public static IEnumerable<string> Method(
        int version,
        string securityFunction,
        string name,
        string procedure = "",
        string mediumRequired = "0",
        string activeMedia = "1",
        (int Max, int First, int Next)? polls = null)
    {
        var fields = new string[version >= 7 ? 26 : 21];
        Array.Fill(fields, string.Empty);
        fields[0] = securityFunction;   // 1 Sicherheitsfunktion, kodiert
        fields[1] = "2";                // 2 TAN-Prozess
        fields[2] = "HHD1.4";           // 3 Technische Identifikation
        fields[3] = procedure;          // 4 DK TAN-Verfahren
        fields[5] = name;               // 6 Name des Zwei-Schritt-Verfahrens
        fields[18] = mediumRequired;    // 19 Bezeichnung des TAN-Mediums erforderlich
        fields[20] = activeMedia;       // 21 Anzahl unterstuetzter aktiver TAN-Medien
        if (version >= 7 && polls is { } p)
        {
            fields[21] = p.Max.ToString(CultureInfo.InvariantCulture);    // 22 Maximale Anzahl Statusabfragen
            fields[22] = p.First.ToString(CultureInfo.InvariantCulture);  // 23 Wartezeit vor erster
            fields[23] = p.Next.ToString(CultureInfo.InvariantCulture);   // 24 Wartezeit vor naechster
        }
        return fields;
    }

    /// <summary>Bankparameter, wie sie nach einer Synchronisation aussehen.</summary>
    public static FinTsBankParameters Merge(params FinTsSegment[] segments)
    {
        var empty = new FinTsBankParameters(0, 0, "0", FinTsMessages.OneStepSecurityFunction, null,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            [], []);
        return FinTsResponseParser.MergeParameters(empty, FinTsResponseParser.Parse(FinTsWire.Serialize(segments)));
    }
}
