using System.Diagnostics;
using FullWorth.Backend.Modules.Purchases;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// The receipt worker polled every second, and every tick opened a connection, took the advisory lock and
/// ran both the stale-lease recovery UPDATE and a claim query — whether or not any job existed. On an idle
/// instance that is two statements a second forever, which is the `UPDATE "ReceiptScanJobs" ... WHERE
/// "Status" = 'processing'` line appearing in the log every couple of seconds.
///
/// It backs off when idle now, so the thing that must not regress is responsiveness: an enqueue has to wake
/// it immediately rather than leaving an upload waiting for the backoff.
/// </summary>
public sealed class ReceiptScanQueueSignalTests
{
    [Fact]
    public async Task An_enqueue_wakes_a_waiting_worker_immediately()
    {
        var signal = new ReceiptScanQueueSignal();
        var stopwatch = Stopwatch.StartNew();

        var waiting = signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        signal.Notify();

        Assert.True(await waiting);
        // Nowhere near the 30 s backoff it was waiting on.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"waited {stopwatch.Elapsed}");
    }

    // The signal is set before the worker gets round to waiting - it must not be lost.
    [Fact]
    public async Task A_signal_raised_before_the_wait_is_not_lost()
    {
        var signal = new ReceiptScanQueueSignal();

        signal.Notify();

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_signal_the_wait_ends_on_its_own_timeout()
    {
        var signal = new ReceiptScanQueueSignal();

        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }

    // Several uploads in a row must not queue up several wake-ups: the worker re-checks the queue anyway,
    // and a backlog of signals would make it spin.
    [Fact]
    public async Task Repeated_signals_collapse_into_one_wake_up()
    {
        var signal = new ReceiptScanQueueSignal();

        signal.Notify();
        signal.Notify();
        signal.Notify();

        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }
}
