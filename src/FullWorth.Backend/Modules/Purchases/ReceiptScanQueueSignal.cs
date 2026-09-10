namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Wakes the receipt worker the moment a job becomes queued, so it does not have to poll to stay responsive.
///
/// It polled once a second, and every tick opened a connection, took the advisory lock, ran the stale-lease
/// recovery UPDATE and a claim query — forever, whether or not any job existed. That is the
/// <c>UPDATE "ReceiptScanJobs" ... WHERE "Status" = 'processing'</c> line appearing in the log every couple
/// of seconds on an idle instance.
///
/// With this, an idle worker backs off instead, and a new upload still starts immediately because the
/// enqueue path signals it. Across replicas the signal does not travel, which is why the backoff has a cap:
/// a job queued elsewhere waits at most that long.
/// </summary>
public sealed class ReceiptScanQueueSignal
{
    private readonly SemaphoreSlim gate = new(0, 1);

    /// <summary>A job is queued. Never blocks, and collapses repeated calls into one wake-up.</summary>
    public void Notify()
    {
        if (gate.CurrentCount == 0)
        {
            try { gate.Release(); }
            catch (SemaphoreFullException) { /* another thread got there first */ }
        }
    }

    /// <summary>
    /// Waits for a signal or the timeout, whichever comes first. Returns true when it was a signal, which
    /// the worker uses only for logging - it re-checks the queue either way.
    /// </summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => gate.WaitAsync(timeout, ct);
}
