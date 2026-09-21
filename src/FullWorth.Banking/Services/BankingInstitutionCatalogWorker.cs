using System.Text.Json;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Services;

public sealed class BankingInstitutionCatalogOptions
{
    public const string SectionName = "BankingInstitutionCatalog";

    /// <summary>
    /// Wie oft der Katalog geholt wird. Banken kommen und gehen in Monaten, nicht in Stunden - ein
    /// Mal am Tag ist fuer ein Verzeichnis reichlich. Der Startdurchlauf fuellt es ohnehin sofort.
    /// </summary>
    public int IntervalMinutes { get; set; } = 1440;
}

/// <summary>
/// Haelt den Enable-Banking-Institutionenkatalog lokal aktuell (#169).
///
/// Nach #165 kam der Gesundheitszustand der Banken aus der Datenbank, die Liste selbst aber
/// weiterhin live vom Anbieter - der Bankdialog hing also immer noch an einem Fremdsystem, und zwar
/// an dem Teil, ohne den man gar keine Bank auswaehlen kann.
///
/// Die Zugangsdaten sind hier andere als beim Statusdienst: der Katalog kommt ueber die
/// Anwendungs-Zugangsdaten eines Profils (<c>/aspsps</c>), der Gesundheitsfeed ueber das
/// Control-Panel-Token. Ein Haus kann das eine haben und das andere nicht.
///
/// Gepflegt werden nur Laender, die gebraucht werden: die schon einmal abgefragten plus die, in
/// denen dieses Haus Verbindungen hat. Jedes Land der Welt durchzugehen erzeugte Anbieterlast fuer
/// Kataloge, die niemand ansieht.
/// </summary>
public sealed class BankingInstitutionCatalogWorker(
    IServiceScopeFactory scopes,
    IOptions<BankingInstitutionCatalogOptions> options,
    IOptionsMonitor<EnableBankingOptions> providerOptions,
    IHostApplicationLifetime lifetime,
    ILogger<BankingInstitutionCatalogWorker> logger) : BackgroundService
{
    private readonly BankingInstitutionCatalogOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForApplicationStartedAsync(lifetime, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // Der Dienst darf an einem Durchlauf nicht sterben: der naechste waere sonst nie.
                logger.LogError(ex, "Institution catalog refresh failed.");
            }

            var interval = TimeSpan.FromMinutes(Math.Clamp(_options.IntervalMinutes, 60, 10080));
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RefreshOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var backend = scope.ServiceProvider.GetRequiredService<FullWorthBackendClient>();
        var sync = scope.ServiceProvider.GetRequiredService<BankSyncService>();

        var principal = await backend.GetProviderPrincipalAsync(ct);
        if (principal is null)
        {
            // Kein Enable-Banking-Profil eingerichtet ist kein Fehler, sondern ein Zustand.
            logger.LogDebug("No Enable Banking profile configured; skipping institution catalog refresh.");
            return;
        }

        var countries = await backend.GetInstitutionCountriesAsync(ct);
        if (countries.Count == 0)
        {
            // Beim allerersten Start kennt niemand ein Land. Das voreingestellte zu nehmen ist die
            // einzige Angabe, die es gibt - danach traegt sich der Katalog selbst.
            var fallback = (providerOptions.CurrentValue.DefaultCountry ?? "DE").Trim().ToUpperInvariant();
            countries = fallback.Length == 2 ? [fallback] : [];
        }

        var caller = new BankingCaller(principal.Value, Guid.Empty);
        foreach (var country in countries)
        {
            if (ct.IsCancellationRequested) return;
            await RefreshCountryAsync(backend, sync, caller, country, ct);
        }
    }

    private async Task RefreshCountryAsync(
        FullWorthBackendClient backend,
        BankSyncService sync,
        BankingCaller caller,
        string country,
        CancellationToken ct)
    {
        JsonElement payload;
        try
        {
            // Ohne PSU-Typ: der Anbieter liefert dann alle Varianten, und genau die braucht der
            // Katalog - eine Bank kann getrennte Privat- und Geschaeftseintraege haben.
            payload = await sync.GetInstitutionsAsync(country, psuType: null, caller, ct);
        }
        catch (EnableBankingProfileNotConfiguredException)
        {
            await backend.RecordInstitutionFailureAsync(country, "banking_profile_not_ready", ct);
            return;
        }
        catch (EnableBankingApiException ex)
        {
            // Nur ein kurzer, bekannter Schluessel: eine Anbieterantwort im Rohzustand hat in der
            // Datenbank nichts zu suchen.
            await backend.RecordInstitutionFailureAsync(country, "provider_request_failed", ct);
            logger.LogWarning(ex, "Institution catalog refresh failed for {Country}.", country);
            return;
        }

        if (!BankingInstitutionPayload.TryRead(payload, country, out var rows))
        {
            await backend.RecordInstitutionFailureAsync(country, "provider_response_invalid", ct);
            logger.LogWarning("Institution catalog response for {Country} had no aspsps array.", country);
            return;
        }

        await backend.ReplaceInstitutionCatalogAsync(country, rows, ct);
        logger.LogInformation("Institution catalog refreshed for {Country}: {Count} entries.", country, rows.Count);
    }

    private static async Task WaitForApplicationStartedAsync(
        IHostApplicationLifetime lifetime,
        CancellationToken cancellationToken)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested)
            return;

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var cancelledRegistration = cancellationToken.Register(() => started.TrySetCanceled(cancellationToken));
        await started.Task;
    }
}
