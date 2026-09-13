namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// Pre-cutover FinTS instance-settings row. It is no longer part of any runtime configuration flow:
/// the canonical product id is FinTs:ProductId from the Web instance configuration.
///
/// The historical entity remains in the pre-cutover EF model only until the migration-baseline squash
/// tracked by #104 removes the old table together with the rest of the superseded migration history.
/// </summary>
public sealed class BankingInstanceSettings
{
    public const string InstanceScopeKey = "instance";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = InstanceScopeKey;
    public string FinTsProductId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Compile-time marker for the pre-cutover host registration. It intentionally exposes no read/write
/// operation. The registration and historical entity are removed with the #104 baseline squash.
/// </summary>
public sealed class BankingInstanceSettingsStore { }

public static class BankingInstanceSettingsEndpoints
{
    /// <summary>
    /// No legacy endpoint is mapped. /internal/banking/settings was a runtime compatibility fallback
    /// and is intentionally gone; FinTS reads only the canonical reloadable configuration path.
    /// </summary>
    public static IEndpointRouteBuilder MapBankingInstanceSettingsEndpoints(this IEndpointRouteBuilder app)
        => app;
}
