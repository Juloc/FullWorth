namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>
/// Einstellungen fuer die konfigurierbare Kursquelle. <see cref="Provider"/> waehlt entweder eine
/// Voreinstellung aus <see cref="MarketDataPresets"/>, <c>custom</c> fuer eine eigene Vorlage oder
/// <c>none</c> - der Ausgangszustand.
///
/// Genau wie bei <see cref="FullWorth.Backend.Modules.Fx.FxRateOptions"/>: nicht erreichbar oder nicht
/// eingerichtet heisst fehlend, nie erfunden. <see cref="Resolve"/> ist die einzige Stelle, die daraus
/// eine tatsaechliche Konfiguration macht, und sie tut das rein - ohne Netzwerk, ohne Seiteneffekt -
/// damit sie ohne Mock testbar bleibt.
/// </summary>
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    /// <summary>Ein Schluessel aus <see cref="MarketDataPresets.Choices"/>: eine Voreinstellung, "custom" oder "none".</summary>
    public string Provider { get; set; } = MarketDataPresets.None;

    // Nur bei Provider="custom" gelesen. Bei einer Voreinstellung gewinnt deren eigene Vorlage -
    // sonst koennte ein alter Eintrag aus einem frueheren "custom" eine Voreinstellung ueberschreiben.
    public string PriceUrl { get; set; } = "";
    public string DatePath { get; set; } = "";
    public string ValuePath { get; set; } = "";
    public string CurrencyPath { get; set; } = "";
    public string SearchUrl { get; set; } = "";
    public string SearchSymbolPath { get; set; } = "";

    /// <summary>Gilt sowohl fuer eine Voreinstellung als auch fuer "custom" - manche Quellen brauchen einen Schluessel, andere keinen.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Wie tief ein einmaliger Backfill zurueckreicht, analog zu FxRateOptions.HistoryBackfillDays.</summary>
    public int BackfillDays { get; set; } = 400;

    /// <summary>
    /// Loest <see cref="Provider"/> zu einer tatsaechlichen Vorlage auf, oder gibt null zurueck, wenn
    /// keine Kursquelle eingerichtet ist. Absichtlich eine reine Funktion: der Provider ruft sie bei
    /// jedem Aufruf neu auf (siehe <see cref="ConfigurableSecurityMarketDataProvider"/>), damit eine
    /// Aenderung im laufenden Betrieb sofort wirkt.
    /// </summary>
    public MarketDataResolved? Resolve()
    {
        var key = (Provider ?? "").Trim();
        if (key.Length == 0 || key == MarketDataPresets.None) return null;

        if (key == MarketDataPresets.Custom)
        {
            // Ausgewaehlt, aber (noch) nicht vollstaendig ausgefuellt - dann lieber "kein Anbieter" als
            // mit einer halben Vorlage gegen eine leere URL zu laufen.
            if (string.IsNullOrWhiteSpace(PriceUrl) || string.IsNullOrWhiteSpace(DatePath) || string.IsNullOrWhiteSpace(ValuePath))
                return null;
            return new MarketDataResolved(
                MarketDataPresets.Custom,
                PriceUrl.Trim(),
                DatePath.Trim(),
                ValuePath.Trim(),
                string.IsNullOrWhiteSpace(CurrencyPath) ? null : CurrencyPath.Trim(),
                string.IsNullOrWhiteSpace(SearchUrl) ? null : SearchUrl.Trim(),
                string.IsNullOrWhiteSpace(SearchSymbolPath) ? null : SearchSymbolPath.Trim(),
                ApiKey ?? "");
        }

        var preset = MarketDataPresets.Find(key);
        // Ein unbekannter Schluessel darf nicht dazu fuehren, dass irgendeine Vorlage geraten wird.
        if (preset is null) return null;
        return new MarketDataResolved(
            preset.Key, preset.PriceUrl, preset.DatePath, preset.ValuePath,
            preset.CurrencyPath, preset.SearchUrl, preset.SearchSymbolPath, ApiKey ?? "");
    }
}

/// <summary>Die aufgeloeste Konfiguration einer Kursquelle - Voreinstellung oder eigene Vorlage, vereinheitlicht.</summary>
public sealed record MarketDataResolved(
    string ProviderKey,
    string PriceUrl,
    string DatePath,
    string ValuePath,
    string? CurrencyPath,
    string? SearchUrl,
    string? SearchSymbolPath,
    string ApiKey);
