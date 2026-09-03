using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Services;

public sealed class BankSyncWorker(IServiceScopeFactory scopes, IOptions<BankingSyncOptions> options, ILogger<BankSyncWorker> logger) : BackgroundService
{
    private readonly BankingSyncOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<BankSyncService>().SyncAllAsync(stoppingToken);
                logger.LogInformation(
                    "Scheduled bank sync finished: {Synced} synced, {Skipped} skipped, {Failed} failed, alreadyRunning={AlreadyRunning}.",
                    result.Synced,
                    result.Skipped,
                    result.Failed,
                    result.AlreadyRunning);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError(ex, "Scheduled bank synchronization failed."); }

            var interval = TimeSpan.FromMinutes(Math.Max(360, _options.IntervalMinutes));
            await Task.Delay(interval, stoppingToken);
        }
    }
}
