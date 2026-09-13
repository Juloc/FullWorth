using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests.Infrastructure;

/// <summary>
/// An <see cref="IOptionsMonitor{T}"/> over a value a test controls.
///
/// The Enable Banking services moved from <c>IOptions&lt;T&gt;</c> to the monitor because
/// <c>IOptions&lt;T&gt;</c> is a process-lifetime snapshot: the redirect URL this installation learns
/// at its first registration reached configuration but never reached the code that uses it. Tests
/// need the same shape, and <see cref="Set"/> is what lets a test prove the difference — change the
/// value and assert the service observes it without being rebuilt.
/// </summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];

    public T CurrentValue { get; private set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return new Subscription(() => _listeners.Remove(listener));
    }

    /// <summary>Publishes a new value, exactly as a reloaded configuration source would.</summary>
    public void Set(T next)
    {
        CurrentValue = next;
        foreach (var listener in _listeners.ToArray()) listener(next, Options.DefaultName);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public static class StaticOptionsMonitor
{
    public static StaticOptionsMonitor<T> For<T>(T value) => new(value);
}
