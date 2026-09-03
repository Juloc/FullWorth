namespace FullWorth.Web.Modules.Recovery;

public sealed class RecoveryCode
{
    public Guid Id { get; set; }
    public Guid AuthUserId { get; set; }
    public byte[] CodeHash { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed record RecoveryCodeStoreStatus(
    int RemainingCount,
    DateTimeOffset? GeneratedAt);

public interface IRecoveryCodeStore
{
    // Must atomically replace all existing codes for this AuthUser with the supplied set.
    Task ReplaceAsync(
        Guid authUserId,
        IReadOnlyCollection<RecoveryCode> recoveryCodes,
        CancellationToken ct = default);

    // Must be one atomic consume operation. Concurrent calls for the same unused hash may return true at most once.
    Task<bool> TryConsumeAsync(
        Guid authUserId,
        byte[] codeHash,
        DateTimeOffset usedAt,
        CancellationToken ct = default);

    Task<RecoveryCodeStoreStatus> GetStatusAsync(
        Guid authUserId,
        CancellationToken ct = default);
}

public interface IRecoveryUserValidator
{
    Task<bool> IsValidRecoveryUserAsync(
        Guid authUserId,
        CancellationToken ct = default);
}
