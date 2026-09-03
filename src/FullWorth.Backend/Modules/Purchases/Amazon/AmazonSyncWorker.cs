using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.Amazon;

public sealed class AmazonSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AmazonIntegrationOptions> options,
    ILogger<AmazonSyncWorker> logger) : BackgroundService
{
    private readonly AmazonIntegrationOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunDueAsync(stoppingToken);
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RunDueAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var amazonStore = scope.ServiceProvider.GetRequiredService<AmazonSqlStore>();
            var dueBefore = DateTimeOffset.UtcNow.AddHours(-Math.Clamp(_options.SyncIntervalHours, 1, 168));
            var connections = await amazonStore.ListDueConnectionsAsync(dueBefore, ct);
            foreach (var connection in connections)
            {
                ct.ThrowIfCancellationRequested();
                using var itemScope = scopeFactory.CreateScope();
                var service = itemScope.ServiceProvider.GetRequiredService<AmazonOrderSyncService>();
                _ = await service.SyncAsync(connection.UserId, connection.FullWorthSpaceId, _options.InitialHistoryDays, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning("Amazon background sync iteration failed: {Type}", ex.GetType().Name);
        }
    }
}
