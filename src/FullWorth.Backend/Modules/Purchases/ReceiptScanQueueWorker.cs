using Npgsql;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Persistent FIFO receipt worker. Browser lifetime is irrelevant: uploads are already stored as
/// Purchases + ReceiptScanJobs before this worker sees them. A PostgreSQL session advisory lock is
/// held for the full processing duration, guaranteeing only one GPT/OCR receipt job globally even if
/// FullWorth later runs multiple backend replicas.
/// </summary>
public sealed class ReceiptScanQueueWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<ReceiptScanQueueWorker> logger) : BackgroundService
{
    // Stable app-specific signed bigint used only by the receipt scan queue.
    private const long QueueAdvisoryLockKey = 0x465752435343414E; // "FWRCSCAN"
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("FullWorth");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogError("Receipt scan worker disabled because the Finance connection string is missing");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            ReceiptScanJobRow? job = null;
            try
            {
                await using var lockConnection = new NpgsqlConnection(connectionString);
                await lockConnection.OpenAsync(stoppingToken);
                if (!await TryAcquireQueueLockAsync(lockConnection, stoppingToken))
                {
                    await Task.Delay(PollDelay, stoppingToken);
                    continue;
                }

                // The global advisory lock proves no other receipt processor is currently active. Any
                // processing row older than the maximum scan lease was left behind by a crashed worker.
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ReceiptScanJobStore>();
                await store.RequeueStaleAsync(DateTimeOffset.UtcNow - ProcessingLease, stoppingToken);

                job = await store.ClaimNextAsync(stoppingToken);
                if (job is null)
                {
                    // Closing lockConnection releases the session advisory lock automatically.
                    await Task.Delay(PollDelay, stoppingToken);
                    continue;
                }

                var processor = scope.ServiceProvider.GetRequiredService<ReceiptScanQueueProcessor>();
                await processor.ProcessAsync(job, stoppingToken);
                // Keep lockConnection alive until ProcessAsync returns. This is the global single-file
                // concurrency guarantee; disposal releases the advisory lock even on exceptions.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Receipt scan worker loop failed{JobSuffix}", job is null ? string.Empty : $" for job {job.Id}");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private static async Task<bool> TryAcquireQueueLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key);";
        command.Parameters.AddWithValue("key", QueueAdvisoryLockKey);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }
}
