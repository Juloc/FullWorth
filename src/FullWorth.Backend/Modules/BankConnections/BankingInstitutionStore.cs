using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// Ein Institut, wie es zwischen Banking und Backend ueber die Leitung geht (#169).
///
/// <paramref name="PsuTypes"/>, <paramref name="Group"/> und <paramref name="AuthMethods"/> sind
/// <see cref="JsonElement"/>, weil sie beim Anbieter eine eigene Form haben: PSU-Typen sind eine
/// Liste, die Gruppe kann Text oder Objekt sein, und die Anmeldeverfahren sind verschachtelte
/// Protokollangaben. Sie hier in C#-Typen zu zwingen hiesse, eine fremde Protokollform nachzubauen -
/// die Oberflaeche reicht sie ohnehin unveraendert durch.
/// </summary>
public sealed record BankingInstitutionRow(
    string Country,
    string Name,
    JsonElement? PsuTypes,
    JsonElement? Group,
    string? Logo,
    bool Beta,
    JsonElement? AuthMethods);

/// <summary>Was die Bankauswahl ueber den Katalog eines Landes erfaehrt.</summary>
/// <param name="Known">Falsch, solange dieses Land nie erfolgreich abgerufen wurde. Eine leere Liste
/// allein waere zweideutig - "der Anbieter kennt hier keine Bank" und "wir haben nie nachgesehen"
/// sehen sonst gleich aus, und der Dialog wuerde beim ersten Mal ein leeres Verzeichnis zeigen,
/// statt zu sagen, dass er noch nichts hat.</param>
public sealed record BankingInstitutionCatalog(
    bool Known,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    IReadOnlyList<BankingInstitutionRow> Institutions);

/// <summary>
/// Der lokale Institutionenkatalog (#169).
///
/// Zwei Regeln tragen diese Datei, und beide sind der Grund, warum es sie gibt:
///
/// <see cref="RecordFailureAsync"/> laesst die Zeilen stehen. Ein Katalog, der bei jedem
/// Anbieterausfall leer waere, haette genau den Fehler, den #169 abstellen soll - dann waere die
/// Bankauswahl wieder von der Erreichbarkeit eines Fremdsystems abhaengig, nur mit einem Umweg
/// dazwischen.
///
/// <see cref="ReplaceCountryAsync"/> legt still statt zu loeschen. Eine bestehende Verbindung zeigt
/// weiterhin auf einen Institutsnamen, und ein Anbieter, der eine Bank fuer einen Durchlauf
/// vergisst, soll sie nicht aus der Geschichte tilgen - meldet er sie wieder, wird dieselbe Zeile
/// wieder aktiv.
/// </summary>
public sealed class BankingInstitutionStore(FullWorthDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BankingInstitutionCatalog> ReadAsync(string country, CancellationToken ct)
    {
        var normalized = Normalize(country);
        var refresh = await db.BankingInstitutionRefreshes.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Country == normalized, ct);

        var rows = await db.BankingInstitutions.AsNoTracking()
            .Where(row => row.Country == normalized && row.IsActive)
            .OrderBy(row => row.Name).ThenBy(row => row.PsuTypesKey)
            .ToListAsync(ct);

        return new BankingInstitutionCatalog(
            Known: refresh?.LastSuccessfulAt is not null,
            LastSuccessfulAt: refresh?.LastSuccessfulAt,
            LastAttemptAt: refresh?.LastAttemptAt,
            LastError: refresh?.LastError,
            Institutions: rows.Select(ToRow).ToList());
    }

    /// <summary>Den Katalog eines Landes aus einem ERFOLGREICHEN Abruf uebernehmen.</summary>
    public async Task ReplaceCountryAsync(
        string country, IReadOnlyList<BankingInstitutionRow> institutions, CancellationToken ct)
    {
        var normalized = Normalize(country);
        var now = DateTimeOffset.UtcNow;

        var incoming = institutions
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => (Row: row, Key: PsuTypesKeyOf(row.PsuTypes)))
            // Derselbe Eintrag zweimal im Feed darf den eindeutigen Index nicht sprengen.
            .GroupBy(entry => (Name: entry.Row.Name.Trim(), entry.Key))
            .Select(group => group.Last())
            .ToList();

        var existing = await db.BankingInstitutions
            .Where(row => row.Country == normalized)
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(row => (row.Name, row.PsuTypesKey));

        foreach (var (row, key) in incoming)
        {
            var name = row.Name.Trim();
            if (byKey.Remove((name, key), out var current))
            {
                Apply(current, row, key, now);
                // Wieder im Feed heisst wieder aktiv - dieselbe Zeile, nicht eine zweite.
                current.IsActive = true;
            }
            else
            {
                var entity = new BankingInstitution { Country = normalized, Name = name };
                Apply(entity, row, key, now);
                db.BankingInstitutions.Add(entity);
            }
        }

        // Was der Anbieter nicht mehr meldet: stillgelegt, nicht geloescht. LastSeenAt bleibt stehen
        // und sagt, wann es zuletzt da war.
        foreach (var gone in byKey.Values)
        {
            if (!gone.IsActive) continue;
            gone.IsActive = false;
            gone.UpdatedAt = now;
        }

        var refresh = await Refresh(normalized, ct);
        refresh.LastAttemptAt = now;
        refresh.LastSuccessfulAt = now;
        refresh.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Einen gescheiterten Abruf vermerken - und die Zeilen in Ruhe lassen.</summary>
    public async Task RecordFailureAsync(string country, string reason, CancellationToken ct)
    {
        var refresh = await Refresh(Normalize(country), ct);
        refresh.LastAttemptAt = DateTimeOffset.UtcNow;
        refresh.LastError = reason.Length > 200 ? reason[..200] : reason;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Welche Laender ueberhaupt gepflegt werden muessen: die, die schon einmal abgefragt wurden,
    /// plus die, in denen dieses Haus Verbindungen hat. Ein Dienst, der stattdessen jedes Land der
    /// Welt durchgeht, erzeugt Anbieterlast fuer Kataloge, die niemand ansieht.
    /// </summary>
    public async Task<List<string>> CountriesToRefreshAsync(CancellationToken ct)
    {
        var known = await db.BankingInstitutionRefreshes.AsNoTracking()
            .Select(row => row.Country).ToListAsync(ct);
        var connected = await db.BankConnections.AsNoTracking()
            .Where(row => row.Country != null && row.Country != string.Empty)
            .Select(row => row.Country!).Distinct().ToListAsync(ct);

        return known.Concat(connected.Select(country => country.ToUpperInvariant()))
            .Where(country => country.Length == 2)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(country => country, StringComparer.Ordinal)
            .ToList();
    }

    private static void Apply(BankingInstitution entity, BankingInstitutionRow row, string key, DateTimeOffset now)
    {
        entity.PsuTypesKey = key;
        entity.PsuTypesJson = Raw(row.PsuTypes) ?? "[]";
        entity.GroupJson = Raw(row.Group);
        entity.LogoUrl = Trim(row.Logo, 500);
        entity.Beta = row.Beta;
        entity.AuthMethodsJson = Raw(row.AuthMethods) ?? "[]";
        entity.LastSeenAt = now;
        entity.UpdatedAt = now;
    }

    private static BankingInstitutionRow ToRow(BankingInstitution entity) => new(
        entity.Country,
        entity.Name,
        Parse(entity.PsuTypesJson),
        Parse(entity.GroupJson),
        entity.LogoUrl,
        entity.Beta,
        Parse(entity.AuthMethodsJson));

    /// <summary>
    /// Der Teil der Identitaet, den der Anbieter ueber die PSU-Typen ausdrueckt. Sortiert, damit aus
    /// derselben Antwort immer derselbe Schluessel entsteht: kaeme die Liste einmal als
    /// ["personal","business"] und einmal umgekehrt, legte der zweite Durchlauf dieselbe Bank noch
    /// einmal an.
    /// </summary>
    private static string PsuTypesKeyOf(JsonElement? psuTypes)
    {
        if (psuTypes is not { ValueKind: JsonValueKind.Array } array) return string.Empty;

        var values = array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal);

        var key = string.Join('|', values);
        return key.Length > 120 ? key[..120] : key;
    }

    private static string? Raw(JsonElement? value) =>
        value is null || value.Value.ValueKind == JsonValueKind.Undefined || value.Value.ValueKind == JsonValueKind.Null
            ? null
            : value.Value.GetRawText();

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        // Der Rueckweg darf nicht an einem Rest aus einer frueheren Anbieterform sterben: eine
        // unlesbare Zeile kostet dann ein Feld und nicht den ganzen Katalog.
        try { return JsonSerializer.Deserialize<JsonElement>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private async Task<BankingInstitutionRefresh> Refresh(string country, CancellationToken ct)
    {
        var refresh = await db.BankingInstitutionRefreshes
            .FirstOrDefaultAsync(row => row.Country == country, ct);
        if (refresh is not null) return refresh;

        refresh = new BankingInstitutionRefresh { Country = country };
        db.BankingInstitutionRefreshes.Add(refresh);
        return refresh;
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static string Normalize(string? country) =>
        string.IsNullOrWhiteSpace(country) ? string.Empty : country.Trim().ToUpperInvariant();
}
