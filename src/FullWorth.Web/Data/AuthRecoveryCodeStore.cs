using FullWorth.Web.Modules.Recovery;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Data;

public sealed class AuthRecoveryCodeStore(AuthDbContext db) : IRecoveryCodeStore
{
    public async Task ReplaceAsync(Guid authUserId, IReadOnlyCollection<RecoveryCode> recoveryCodes, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.RecoveryCodes.Where(x => x.AuthUserId == authUserId).ExecuteDeleteAsync(ct);
        db.RecoveryCodes.AddRange(recoveryCodes);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<bool> TryConsumeAsync(Guid authUserId, byte[] codeHash, DateTimeOffset usedAt, CancellationToken ct = default)
    {
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE auth.\"RecoveryCodes\" SET \"UsedAt\" = {usedAt} WHERE \"AuthUserId\" = {authUserId} AND \"CodeHash\" = {codeHash} AND \"UsedAt\" IS NULL",
            ct);
        return affected == 1;
    }

    public async Task<RecoveryCodeStoreStatus> GetStatusAsync(Guid authUserId, CancellationToken ct = default)
    {
        var remaining = await db.RecoveryCodes.CountAsync(x => x.AuthUserId == authUserId && x.UsedAt == null, ct);
        var generatedAt = await db.RecoveryCodes
            .Where(x => x.AuthUserId == authUserId)
            .Select(x => (DateTimeOffset?)x.CreatedAt)
            .MaxAsync(ct);
        return new RecoveryCodeStoreStatus(remaining, generatedAt);
    }
}
