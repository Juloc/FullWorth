using FullWorth.Backend.Modules.FullWorthSpaces;

namespace FullWorth.Backend.Modules.Accounts;

public sealed class AccountFullWorthSpaceMembership(FullWorthSpaceStore fullWorthSpaces) : IAccountFullWorthSpaceMembership
{
    public Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        fullWorthSpaces.IsMemberAsync(userId, fullWorthSpaceId, ct);
}
