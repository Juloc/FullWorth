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
    ReceiptScanQueueSignal signal,
    ILogger<ReceiptScanQueueWorker> logger) : BackgroundService
{
    // Stable app-specific signed bigint used only by the receipt scan queue.
    private const long QueueAdvisoryLockKey = 0x465752435343414E; // "FWRCSCAN"
    private static readonly TimeSpan ProcessingLease = TimeSpan.FromMinutes(6);

    // An idle worker used to poll every second, and every tick opened a connection, took the advisory
    // lock and ran BOTH the stale-lease recovery UPDATE and a claim query - whether or not any job
    // existed. On an idle instance that is two statements a second, forever, which is the
    // ReceiptScanJobs UPDATE showing up in the log every couple of seconds.
    //
    // Now it waits, doubling up to a cap, and an upload wakes it immediately through the signal, so
    // responsiveness is unchanged. The cap only bounds a job queued by ANOTHER replica, where the
    // in-process signal cannot reach.
    private static readonly TimeSpan MinimumIdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumIdleDelay = TimeSpan.FromSeconds(30);

    // A stale processing row is left behind by a crashed worker, so it cannot appear faster than the
    // lease. Checking it once a minute is ample; it used to run on every single poll.
    private static readonly TimeSpan StaleCheckInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("FullWorth");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogError("Receipt scan worker disabled because the Finance connection string is missing");
            return;
        }

        var idleDelay = MinimumIdleDelay;
        var nextStaleCheck = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            ReceiptScanJobRow? job = null;
            try
            {
                await using var lockConnection = new NpgsqlConnection(connectionString);
                await lockConnection.OpenAsync(stoppingToken);
                if (!await TryAcquireQueueLockAsync(lockConnection, stoppingToken))
                {
                    // Another processor holds the queue; nothing to do until it releases.
                    await WaitForWorkAsync(MaximumIdleDelay, stoppingToken);
                    continue;
                }

                // The global advisory lock proves no other receipt processor is currently active. Any
                // processing row older than the maximum scan lease was left behind by a crashed worker.
                await using var scope = scopes.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<ReceiptScanJobStore>();
                var now = DateTimeOffset.UtcNow;
                if (now >= nextStaleCheck)
                {
                    await store.RequeueStaleAsync(now - ProcessingLease, stoppingToken);
                    nextStaleCheck = now + StaleCheckInterval;
                }

                job = await store.ClaimNextAsync(stoppingToken);
                if (job is null)
                {
                    // Closing lockConnection releases the session advisory lock automatically.
                    await WaitForWorkAsync(idleDelay, stoppingToken);
                    idleDelay = idleDelay >= MaximumIdleDelay
                        ? MaximumIdleDelay
                        : TimeSpan.FromTicks(Math.Min(idleDelay.Ticks * 2, MaximumIdleDelay.Ticks));
                    continue;
                }

                // Work exists, so react immediately again after it.
                idleDelay = MinimumIdleDelay;

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

    /// <summary>Waits for an enqueue signal or the delay, whichever comes first.</summary>
    private async Task WaitForWorkAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await signal.WaitAsync(delay, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static async Task<bool> TryAcquireQueueLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key);";
        command.Parameters.AddWithValue("key", QueueAdvisoryLockKey);
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }
}
