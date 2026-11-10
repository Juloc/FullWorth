using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Paperless;

/// <summary>
/// Laeuft in festem Abstand ueber alle automatischen Presets und uebergibt jedes dem Importer seiner
/// Art. Was ein Dokument wird - ein Beleg, eine Lohnabrechnung - weiss dieser Dienst nicht.
/// </summary>
public sealed class PaperlessAutoImportWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PaperlessOptions> options,
    ILogger<PaperlessAutoImportWorker> logger) : BackgroundService
{
    private readonly TimeSpan interval = TimeSpan.FromMinutes(
        Math.Clamp(options.Value.PaperlessAutoImportIntervalMinutes, 15, 24 * 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Keep startup quiet. Automatic presets establish their baseline when saved,
        // so there is no reason to hit Paperless immediately during every deployment.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<PaperlessStore>();
            var paperless = scope.ServiceProvider.GetRequiredService<PaperlessService>();
            var client = scope.ServiceProvider.GetRequiredService<PaperlessClient>();
            var importers = scope.ServiceProvider.GetServices<IPaperlessPresetImporter>()
                .SelectMany(importer => importer.Kinds.Select(kind => (kind, importer))).ToDictionary(x => x.kind, x => x.importer);
            var presets = await store.ListAutoImportPresetsAsync(ct);
            if (presets.Count == 0) return;

            foreach (var group in presets.GroupBy(x => x.FullWorthSpaceId))
            {
                ct.ThrowIfCancellationRequested();

                // One reachability check per Paperless connection per interval.
                // Only after it succeeds do we evaluate automatic presets.
                PaperlessConnection? connection = null;
                (bool Success, string? ServerVersion, string? Error) health;
                try
                {
                    connection = await paperless.RequireAsync(group.Key, ct);
                    health = await client.TestAsync(connection.BaseUrl, connection.Token, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    health = (false, null, ex.Message);
                }

                if (!health.Success)
                {
                    var error = health.Error ?? "Paperless is not reachable.";
                    foreach (var preset in group)
                        await store.UpdatePaperlessPresetCheckAsync(preset.Id, null, false, error, ct);
                    continue;
                }

                foreach (var preset in group)
                {
                    ct.ThrowIfCancellationRequested();
                    var outcome = await importers[preset.Kind].ImportAsync(preset, connection!, ct);
                    await store.UpdatePaperlessPresetCheckAsync(
                        preset.Id, outcome.MaxSeenDocumentId, outcome.Imported, outcome.Error, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Paperless automatic import cycle failed.");
        }
    }
}
