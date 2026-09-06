using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.ReceiptImports;

public sealed class PaperlessAutoImportWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ReceiptImportOptions> options,
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
            var store = scope.ServiceProvider.GetRequiredService<ReceiptImportStore>();
            var service = scope.ServiceProvider.GetRequiredService<ReceiptImportService>();
            var presets = await store.ListAutoImportPresetsAsync(ct);
            if (presets.Count == 0) return;

            foreach (var group in presets.GroupBy(x => x.FullWorthSpaceId))
            {
                ct.ThrowIfCancellationRequested();

                (bool Success, string? ServerVersion, string? Error) health;
                try
                {
                    health = await service.TestPaperlessAsync(group.Key, ct);
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

                // One reachability check per Paperless connection per interval.
                // Only after it succeeds do we evaluate automatic presets.
                foreach (var preset in group)
                {
                    ct.ThrowIfCancellationRequested();
                    await service.RunPaperlessAutoImportPresetAsync(preset, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Paperless automatic receipt import cycle failed.");
        }
    }
}
