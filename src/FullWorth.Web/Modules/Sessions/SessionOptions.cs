namespace FullWorth.Web.Modules.Sessions;

public sealed class SessionOptions
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan AbsoluteLifetime { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan TouchInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan CleanupRetention { get; init; } = TimeSpan.FromDays(30);

    public void Validate()
    {
        if (IdleTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Session idle timeout must be positive.");
        if (AbsoluteLifetime <= TimeSpan.Zero)
            throw new InvalidOperationException("Session absolute lifetime must be positive.");
        if (TouchInterval <= TimeSpan.Zero || TouchInterval > IdleTimeout)
            throw new InvalidOperationException("Session touch interval must be positive and no longer than the idle timeout.");
        if (CleanupRetention < TimeSpan.Zero)
            throw new InvalidOperationException("Session cleanup retention cannot be negative.");
    }
}
