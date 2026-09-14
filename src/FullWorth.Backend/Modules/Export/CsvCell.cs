using System.Globalization;

namespace FullWorth.Backend.Modules.Export;

/// <summary>
/// Wie ein Wert in einer CSV-Zelle aussieht. Vier Zeilen, aber sie entscheiden, ob ein Betrag als
/// "1234.56" oder "1.234,56" in der Datei landet - und das ist keine Kosmetik: ein Export, den
/// Tabellenprogramme unterschiedlich lesen, ist falsch.
///
/// Sie lagen in der Endpunktdatei und wurden auch vom Anlagenteil gebraucht, der jetzt im Store
/// steht.
/// </summary>
internal static class CsvCell
{
    public static List<string[]> Table(params string[][] rows) => rows.ToList();
    public static string Num(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Bool(bool value) => value ? "true" : "false";
    public static string Date(DateOnly? value) => value?.ToString("yyyy-MM-dd") ?? "";
}
