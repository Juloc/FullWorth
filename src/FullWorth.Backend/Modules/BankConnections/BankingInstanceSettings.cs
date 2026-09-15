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

