using Npgsql;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Persistent receipt worker with two lanes. Browser lifetime is irrelevant: uploads are already stored as
/// Purchases + ReceiptScanJobs before this worker sees them.
///
/// The AI lane is strictly serial: a PostgreSQL session advisory lock is held for the full processing
/// duration, guaranteeing only one GPT/OCR receipt job globally even if FullWorth later runs multiple
/// backend replicas. The fast lane looks at every new job first and reads a digital PDF locally in
/// milliseconds - so such a receipt never waits behind a slow model call. Whatever it cannot read is
/// handed to the AI lane; the claim is SKIP LOCKED, so the two lanes never take the same job.
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

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RunAiLaneAsync(stoppingToken), RunFastLaneAsync(stoppingToken));

    private async Task RunFastLaneAsync(CancellationToken stoppingToken)
    {
        var idleDelay = MinimumIdleDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            ReceiptScanJobRow? job = null;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                job = await scope.ServiceProvider.GetRequiredService<ReceiptScanJobStore>().ClaimNextAsync(null, stoppingToken);
                if (job is null)
                {
                    await WaitForWorkAsync(aiLane: false, idleDelay, stoppingToken);
                    idleDelay = TimeSpan.FromTicks(Math.Min(idleDelay.Ticks * 2, MaximumIdleDelay.Ticks));
                    continue;
                }

                idleDelay = MinimumIdleDelay;
                await scope.ServiceProvider.GetRequiredService<ReceiptScanQueueProcessor>().ProcessAsync(job, stoppingToken, digitalOnly: true);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Receipt scan fast lane failed{JobSuffix}", job is null ? string.Empty : $" for job {job.Id}");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task RunAiLaneAsync(CancellationToken stoppingToken)
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
                    await WaitForWorkAsync(aiLane: true, MaximumIdleDelay, stoppingToken);
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

                job = await store.ClaimNextAsync(ReceiptScanLanes.Ai, stoppingToken);
                if (job is null)
                {
                    // Closing lockConnection releases the session advisory lock automatically.
                    await WaitForWorkAsync(aiLane: true, idleDelay, stoppingToken);
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
    private async Task WaitForWorkAsync(bool aiLane, TimeSpan delay, CancellationToken ct)
    {
        try { await signal.WaitAsync(aiLane, delay, ct); }
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
