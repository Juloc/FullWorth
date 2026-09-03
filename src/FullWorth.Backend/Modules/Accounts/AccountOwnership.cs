using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

[PrimaryKey(nameof(AccountId), nameof(UserId))]
public sealed class AccountOwner
{
    public Guid AccountId { get; set; }
    public Guid UserId { get; set; }

    [MaxLength(16)]
    public string OwnershipType { get; set; } = AccountOwnershipTypes.Owner;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public FinanceAccount Account { get; set; } = null!;
}

public static class AccountOwnershipTypes
{
    public const string Owner = "owner";
    public const string Viewer = "viewer";

    public static bool IsValid(string ownershipType) =>
        ownershipType == Owner || ownershipType == Viewer;
}

public sealed record AccountOwnerDto(Guid AccountId, Guid UserId, string OwnershipType, DateTimeOffset CreatedAt);
public sealed record AddAccountOwnerRequest(Guid UserId, string OwnershipType);

public enum AccountOwnerChangeResult
{
    Added,
    Removed,
    AccessDenied,
    TargetNotFullWorthSpaceMember,
    Duplicate,
    InvalidOwnershipType,
    LastOwner,
    NotFound
}

public interface IAccountFullWorthSpaceMembership
{
    Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct);
}

public sealed class AccountService(AccountStore store, IAccountFullWorthSpaceMembership membership)
{
    public async Task<IReadOnlyList<AccountOwnerDto>?> ListOwnersAsync(
        Guid actingUserId,
        Guid fullWorthSpaceId,
        Guid accountId,
        CancellationToken ct)
    {
        if (!await CanUserAccessAsync(actingUserId, fullWorthSpaceId, accountId, ct)) return null;
        return await store.ListOwnersAsync(accountId, fullWorthSpaceId, ct);
    }

    public async Task<bool> CanUserAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct)
    {
        if (!await membership.IsMemberAsync(userId, fullWorthSpaceId, ct)) return false;
        return await store.HasAccessAsync(userId, fullWorthSpaceId, accountId, ct);
    }

    public async Task<bool> CanUserEditAsync(Guid userId, Guid fullWorthSpaceId, Guid accountId, CancellationToken ct)
    {
        if (!await membership.IsMemberAsync(userId, fullWorthSpaceId, ct)) return false;
        return await store.HasEditAccessAsync(userId, fullWorthSpaceId, accountId, ct);
    }

    public async Task<AccountOwnerChangeResult> AddOwnerAsync(
        Guid actingUserId,
        Guid fullWorthSpaceId,
        Guid accountId,
        Guid userId,
        string ownershipType,
        CancellationToken ct)
    {
        if (!AccountOwnershipTypes.IsValid(ownershipType)) return AccountOwnerChangeResult.InvalidOwnershipType;
        if (!await CanUserEditAsync(actingUserId, fullWorthSpaceId, accountId, ct)) return AccountOwnerChangeResult.AccessDenied;
        if (!await membership.IsMemberAsync(userId, fullWorthSpaceId, ct)) return AccountOwnerChangeResult.TargetNotFullWorthSpaceMember;
        if (await store.OwnerExistsAsync(userId, fullWorthSpaceId, accountId, ct)) return AccountOwnerChangeResult.Duplicate;

        var added = await store.InsertOwnerAsync(fullWorthSpaceId, accountId, userId, ownershipType, actingUserId, ct);
        return added ? AccountOwnerChangeResult.Added : AccountOwnerChangeResult.NotFound;
    }

    public async Task<AccountOwnerChangeResult> RemoveOwnerAsync(
        Guid actingUserId,
        Guid fullWorthSpaceId,
        Guid accountId,
        Guid userId,
        CancellationToken ct)
    {
        if (!await CanUserEditAsync(actingUserId, fullWorthSpaceId, accountId, ct)) return AccountOwnerChangeResult.AccessDenied;

        var target = await store.GetOwnerAsync(userId, fullWorthSpaceId, accountId, ct);
        if (target is null) return AccountOwnerChangeResult.NotFound;

        if (target.OwnershipType == AccountOwnershipTypes.Owner &&
            await store.CountOwnersAsync(fullWorthSpaceId, accountId, ct) <= 1)
        {
            return AccountOwnerChangeResult.LastOwner;
        }

        await store.DeleteOwnerAsync(target, actingUserId, fullWorthSpaceId, ct);
        return AccountOwnerChangeResult.Removed;
    }
}
