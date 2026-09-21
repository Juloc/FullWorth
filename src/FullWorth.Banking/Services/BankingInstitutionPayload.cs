using System.Text.Json;
using FullWorth.Banking.Backend;

namespace FullWorth.Banking.Services;

/// <summary>
/// Liest die <c>aspsps</c>-Antwort von Enable Banking in Katalogzeilen (#169).
///
/// Eigene Datei, weil es zwei Aufrufer gibt: den Hintergrunddienst, der den Katalog aktuell haelt,
/// und den Endpunkt, der ihn beim allerersten Mal fuellt. Zweimal kopiert waere es zweimal zu
/// pflegen - und der naechste Anbieterwechsel landete in einer der beiden Kopien.
/// </summary>
public static class BankingInstitutionPayload
{
    /// <summary>
    /// Gibt <c>false</c> zurueck, wenn die Antwort nicht die erwartete Form hatte.
    ///
    /// Das ist der Unterschied, der zaehlt: "der Anbieter kennt in diesem Land keine Bank" ist eine
    /// leere Liste und gueltig, "die Antwort war anders als gedacht" ist ein Fehler. Nur der erste
    /// Fall darf den Katalog ersetzen - der zweite wuerde ihn leeren, und die Bankauswahl waere
    /// danach leer, weil der Anbieter sein Format geaendert hat.
    /// </summary>
    public static bool TryRead(JsonElement payload, string country, out List<BankingInstitutionRowDto> rows)
    {
        rows = [];
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("aspsps", out var aspsps)
            || aspsps.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in aspsps.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var name = Text(item, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            rows.Add(new BankingInstitutionRowDto(
                // Das Land aus der Antwort gewinnt, wenn es da ist: der Anbieter weiss es besser als
                // die Abfrage, mit der gefragt wurde.
                Country: Text(item, "country") is { Length: 2 } reported ? reported.ToUpperInvariant() : country,
                Name: name!,
                PsuTypes: Property(item, "psu_types"),
                Group: Property(item, "group"),
                Logo: Text(item, "logo"),
                Beta: item.TryGetProperty("beta", out var beta) && beta.ValueKind == JsonValueKind.True,
                AuthMethods: Property(item, "auth_methods")));
        }
        return true;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// <c>Clone()</c> ist Pflicht: ein <see cref="JsonElement"/> zeigt in das Dokument, aus dem er
    /// stammt, und das wird freigegeben, sobald die Antwort verarbeitet ist. Ohne die Kopie traegt
    /// die Zeile einen Verweis auf befreiten Speicher.
    /// </summary>
    private static JsonElement? Property(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? value.Clone()
            : null;
}
