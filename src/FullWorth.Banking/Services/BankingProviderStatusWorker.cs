using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Services;

public sealed class BankingProviderStatusOptions
{
    public const string SectionName = "BankingProviderStatus";

    /// <summary>
    /// Wie oft der Gesundheitsfeed geholt wird. Der Anbieter veroeffentlicht Tagesstatistiken, also
    /// waere haeufiger nur mehr Last ohne mehr Wahrheit. Sechs Stunden geben trotzdem vier Chancen am
    /// Tag, einen Ausfall zu bemerken.
    /// </summary>
    public int IntervalMinutes { get; set; } = 360;
}

/// <summary>
/// Haelt den Enable-Banking-Gesundheitsfeed lokal aktuell (#165).
///
/// Vorher holte jeder Aufruf von <c>GET /api/banking/provider-status</c> den Zustand live aus dem
/// Control Panel, und der Bankdialog wartete darauf. Jetzt liest die Oberflaeche nur noch, was hier
/// abgelegt wurde - der Abruf haengt nicht mehr an einem Browser-Request, und ein langsames oder
/// abgeschaltetes Control Panel macht die Bankauswahl nicht mehr langsam.
///
/// Zwei Dinge sind hier Absicht:
///
/// Ein Fehlschlag ersetzt die Zeilen NICHT. Er vermerkt nur den Versuch samt bereinigtem Grund; der
/// letzte erfolgreiche Stand bleibt stehen, und die Oberflaeche sieht an <c>LastSuccessfulAt</c>, wie
/// alt er ist. Andernfalls machte ein zweiminuetiger Anbieterausfall aus "alle Banken erreichbar" ein
/// "Zustand unbekannt", ohne dass sich an einer Bank etwas geaendert haette.
///
/// Es laeuft immer nur ein Abruf. Der Dienst ist der einzige Ausloeser und arbeitet der Reihe nach -
/// zwei gleichzeitige Abrufe desselben Feeds waeren nur doppelte Last beim Anbieter.
/// </summary>
public sealed class BankingProviderStatusWorker(
    IServiceScopeFactory scopes,
    IOptions<BankingProviderStatusOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<BankingProviderStatusWorker> logger) : BackgroundService
{
    private readonly BankingProviderStatusOptions _options = options.Value;

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
                logger.LogError(ex, "Provider status refresh failed.");
            }

            var interval = TimeSpan.FromMinutes(Math.Clamp(_options.IntervalMinutes, 15, 1440));
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RefreshOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var backend = scope.ServiceProvider.GetRequiredService<FullWorthBackendClient>();
        var status = scope.ServiceProvider.GetRequiredService<EnableBankingControlPanelStatusService>();

        var principal = await backend.GetControlPanelPrincipalAsync(ct);
        if (principal is null)
        {
            // Kein Control-Panel-Zugang eingerichtet ist kein Fehler, sondern ein Zustand: eine
            // Installation ohne Enable Banking soll hier nichts ablegen und nichts melden.
            logger.LogDebug("No Enable Banking control panel access configured; skipping status refresh.");
            return;
        }

        // Ohne Land: der Feed kommt ohnehin in einem Zug fuer alle Laender, gefiltert wird erst beim
        // Lesen. Ihn je Land zu holen waere derselbe Abruf mehrfach.
        var view = await status.GetTodayAsync(principal.Value, country: null, ct);
        if (!view.Available)
        {
            await backend.RecordProviderStatusFailureAsync(view.Reason ?? "provider_status_unavailable", ct);
            logger.LogInformation("Provider status refresh unavailable: {Reason}.", view.Reason ?? "unknown");
            return;
        }

        var rows = view.Statuses
            .Select(row => new BankingProviderStatusRowDto(row.Country, row.Brand, row.PsuType, row.Status))
            .ToList();
        await backend.ReplaceProviderStatusAsync(rows, ct);
        logger.LogInformation("Provider status refreshed: {Count} institutions.", rows.Count);
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
