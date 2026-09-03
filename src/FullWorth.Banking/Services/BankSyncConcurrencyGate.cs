namespace FullWorth.Banking.Services;

public sealed class BankSyncConcurrencyGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    public async Task<IDisposable?> TryEnterAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
            return null;
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
