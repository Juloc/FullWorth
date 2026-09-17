using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>
/// Kursquelle, deren URL-Vorlagen und JSON-Pfade aus <see cref="MarketDataOptions"/> kommen - entweder
/// eine Voreinstellung aus <see cref="MarketDataPresets"/> oder eine eigene ("custom") Vorlage. Holt nur
/// - gespeichert wird an anderer Stelle, in <see cref="SecurityMarketDataService"/>.
///
/// Als Singleton registriert (der HTTP-Aufruf braucht keinen Scope, keinen DbContext), deshalb steht
/// hier <see cref="IOptionsMonitor{T}"/> und nicht <see cref="IOptions{T}"/>: der Betreiber aendert die
/// Kursquelle im laufenden Betrieb im Admin-UI, und <c>IOptions&lt;T&gt;</c> wuerde den Stand des
/// Prozessstarts fuer die gesamte Laufzeit einfrieren. <see cref="MarketDataOptions.Resolve"/> wird
/// deshalb bei jedem Aufruf neu ausgewertet, nie zwischengespeichert.
/// </summary>
public sealed class ConfigurableSecurityMarketDataProvider(
    HttpClient http,
    IOptionsMonitor<MarketDataOptions> options,
    ILogger<ConfigurableSecurityMarketDataProvider> logger)
    : ISecurityPriceProvider, ISecurityMetadataProvider
{
    /// <summary>"none", solange keine Kursquelle eingerichtet ist - so filtert ihn <see cref="SecurityMarketDataService"/> selbst heraus.</summary>
    public string ProviderKey => options.CurrentValue.Resolve()?.ProviderKey ?? MarketDataPresets.None;

    public bool CanHandle(SecurityMarketDescriptor security) =>
        options.CurrentValue.Resolve() is not null &&
        (!string.IsNullOrWhiteSpace(security.Isin) || !string.IsNullOrWhiteSpace(security.Ticker));

    public async Task<IReadOnlyList<SecurityPriceCandidate>> GetPricesAsync(
        SecurityMarketDescriptor security, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var resolved = options.CurrentValue.Resolve();
        if (resolved is null) return [];

        try
        {
            var symbol = !string.IsNullOrWhiteSpace(security.Ticker)
                ? security.Ticker
                : await ResolveSymbolAsync(resolved, security.Isin, security.Wkn, ct);
            // Kein Symbol ermittelbar - leere Liste statt eines geratenen Werts.
            if (string.IsNullOrWhiteSpace(symbol)) return [];

            var url = ApplyTemplate(resolved.PriceUrl, symbol, security.Isin, security.Wkn,
                UnixSeconds(from), UnixSeconds(to), resolved.ApiKey);
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Nie den Antworttext loggen: er ist fremder Inhalt, und die URL kann den API-Schluessel
                // enthalten. Statuscode und Anbieter-Schluessel reichen zur Diagnose.
                logger.LogWarning(
                    "Market-data price fetch failed with status {Status} for provider {Provider}.",
                    (int)response.StatusCode, resolved.ProviderKey);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var dates = JsonPath.ReadArray(document.RootElement, resolved.DatePath);
            var values = JsonPath.ReadArray(document.RootElement, resolved.ValuePath);
            var currency = ResolveCurrency(document.RootElement, resolved.CurrencyPath, security.Currency);

            var candidates = new List<SecurityPriceCandidate>();
            for (var i = 0; i < Math.Min(dates.Count, values.Count); i++)
            {
                var date = ParseDate(dates[i]);
                var price = ParseValue(values[i]);
                // Fehlende Tage (z.B. ausschuettende ETFs ohne Kurs an einem Feiertag) einfach ueberspringen,
                // nichts erfinden. Nicht-positive Preise filtert der aufrufende Dienst zwar auch, aber es
                // spricht nichts dafuer, hier schon Muell zurueckzugeben.
                if (date is null || price is null || price <= 0) continue;
                candidates.Add(new SecurityPriceCandidate(date.Value, price.Value, currency));
            }
            return candidates;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Auch die Ausnahme selbst nicht loggen: manche HTTP-Ausnahmen zitieren die angefragte URL,
            // und die kann den API-Schluessel enthalten. Statuscode/Anbieter oben deckt den Regelfall ab,
            // ein Netzwerkfehler ohne Antwort bleibt hier bewusst unspezifisch.
            logger.LogWarning(
                "Market-data price fetch threw for provider {Provider}; treating as unavailable.",
                resolved.ProviderKey);
            return [];
        }
    }

    public async Task<IReadOnlyList<SecurityMetadataCandidate>> SearchAsync(string query, CancellationToken ct)
    {
        var resolved = options.CurrentValue.Resolve();
        if (resolved?.SearchUrl is null || resolved.SearchSymbolPath is null || string.IsNullOrWhiteSpace(query))
            return [];

        try
        {
            // Bescheiden gehalten (siehe Auftrag): ein Kandidat aus dem Symbol, keine vollstaendige
            // Metadatenabfrage. Wer mehr braucht, sucht ueber die Voreinstellung, die die Suche liefert.
            var url = ApplyTemplate(resolved.SearchUrl, null, query, null, null, null, resolved.ApiKey);
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Market-data metadata search failed with status {Status} for provider {Provider}.",
                    (int)response.StatusCode, resolved.ProviderKey);
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var symbol = ReadString(document.RootElement, resolved.SearchSymbolPath);
            if (string.IsNullOrWhiteSpace(symbol)) return [];

            return [new SecurityMetadataCandidate(resolved.ProviderKey, symbol, query, null, symbol, "other", "", null)];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogWarning(
                "Market-data metadata search threw for provider {Provider}; treating as unavailable.",
                resolved.ProviderKey);
            return [];
        }
    }

    private async Task<string?> ResolveSymbolAsync(MarketDataResolved resolved, string? isin, string? wkn, CancellationToken ct)
    {
        if (resolved.SearchUrl is null || resolved.SearchSymbolPath is null || string.IsNullOrWhiteSpace(isin))
            return null;

        using var response = await http.GetAsync(
            ApplyTemplate(resolved.SearchUrl, null, isin, wkn, null, null, resolved.ApiKey), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Market-data symbol search failed with status {Status} for provider {Provider}.",
                (int)response.StatusCode, resolved.ProviderKey);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ReadString(document.RootElement, resolved.SearchSymbolPath);
    }

    private static string? ReadString(JsonElement root, string path) =>
        JsonPath.Read(root, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static string ResolveCurrency(JsonElement root, string? currencyPath, string fallback)
    {
        if (currencyPath is null) return fallback;
        return ReadString(root, currencyPath) is { Length: > 0 } found ? found : fallback;
    }

    /// <summary>
    /// Ersetzt <c>{symbol}</c>, <c>{isin}</c>, <c>{wkn}</c>, <c>{from}</c>, <c>{to}</c> und <c>{apiKey}</c>
    /// in einer URL-Vorlage. Alles ausser den beiden Unix-Zeitstempeln wird URL-escaped - Isin/Wkn/Symbol
    /// kommen aus unseren eigenen Daten, aber der API-Schluessel eines Betreibers koennte Zeichen
    /// enthalten, die eine Query-String-Komponente sonst zerbrechen wuerden.
    /// </summary>
    private static string ApplyTemplate(
        string template, string? symbol, string? isin, string? wkn, long? fromUnix, long? toUnix, string? apiKey)
    {
        var result = template;
        if (symbol is not null) result = result.Replace("{symbol}", Uri.EscapeDataString(symbol));
        if (isin is not null) result = result.Replace("{isin}", Uri.EscapeDataString(isin));
        if (wkn is not null) result = result.Replace("{wkn}", Uri.EscapeDataString(wkn));
        if (fromUnix is not null) result = result.Replace("{from}", fromUnix.Value.ToString());
        if (toUnix is not null) result = result.Replace("{to}", toUnix.Value.ToString());
        if (apiKey is not null) result = result.Replace("{apiKey}", Uri.EscapeDataString(apiKey));
        return result;
    }

    private static long UnixSeconds(DateOnly date) =>
        new DateTimeOffset(DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Datumsarrays kommen als Unix-Sekunden (Zahl, z.B. Yahoo) oder als ISO-Datumstext - beide Formen sind ueblich.</summary>
    private static DateOnly? ParseDate(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var seconds) =>
            DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime),
        JsonValueKind.String when DateOnly.TryParse(element.GetString(), out var parsed) => parsed,
        _ => null,
    };

    private static decimal? ParseValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value) ? value : null;
}
