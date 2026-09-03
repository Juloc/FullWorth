namespace FullWorth.Backend.Tests.Security;

public enum TestSpaceRole
{
    Owner,
    Member
}

public enum TestAccountAccess
{
    Owner,
    Viewer
}

public sealed record SecurityUserRef(Guid Id, string Name);
public sealed record SecuritySpaceRef(Guid Id, string Name);
public sealed record SecurityMembership(Guid FullWorthSpaceId, Guid UserId, TestSpaceRole Role);
public sealed record SecurityAccountGrant(Guid UserId, TestAccountAccess Access);

public sealed record SecurityAccountRef(
    Guid Id,
    Guid FullWorthSpaceId,
    string Name,
    IReadOnlyList<SecurityAccountGrant> Grants);

public sealed record SecurityResourceRef(
    Guid Id,
    string ResourceType,
    Guid FullWorthSpaceId,
    Guid? AccountId = null,
    Guid? ParentId = null);

public sealed record SecurityTopology(
    SecurityUserRef UserA,
    SecurityUserRef UserB,
    SecurityUserRef UserC,
    SecuritySpaceRef SpaceA,
    SecuritySpaceRef SpaceB,
    IReadOnlyList<SecurityMembership> Memberships,
    IReadOnlyList<SecurityAccountRef> Accounts,
    IReadOnlyList<SecurityResourceRef> Resources)
{
    public bool IsMember(SecurityUserRef user, SecuritySpaceRef space) =>
        Memberships.Any(x => x.UserId == user.Id && x.FullWorthSpaceId == space.Id);

    public TestAccountAccess? AccountAccess(SecurityUserRef user, SecurityAccountRef account) =>
        account.Grants.SingleOrDefault(x => x.UserId == user.Id)?.Access;

    public SecurityAccountRef Account(string name) =>
        Accounts.Single(x => x.Name == name);

    public SecurityResourceRef Resource(string resourceType) =>
        Resources.Single(x => x.ResourceType == resourceType);
}

public static class MultiUserTestData
{
    public static readonly SecurityUserRef UserA = new(Guid.Parse("10000000-0000-0000-0000-000000000001"), "UserA");
    public static readonly SecurityUserRef UserB = new(Guid.Parse("10000000-0000-0000-0000-000000000002"), "UserB");
    public static readonly SecurityUserRef UserC = new(Guid.Parse("10000000-0000-0000-0000-000000000003"), "UserC");

    public static readonly SecuritySpaceRef SpaceA = new(Guid.Parse("20000000-0000-0000-0000-000000000001"), "SpaceA");
    public static readonly SecuritySpaceRef SpaceB = new(Guid.Parse("20000000-0000-0000-0000-000000000002"), "SpaceB");

    /// <summary>
    /// Cross-space fixture. UserA belongs only to SpaceA and UserB belongs only to SpaceB.
    /// Use this topology for true foreign-space UUID/aggregate isolation tests.
    /// </summary>
    public static SecurityTopology CreateIsolationTopology()
    {
        var accountA = Account(
            "AccountA",
            "30000000-0000-0000-0000-000000000001",
            SpaceA,
            Grant(UserA, TestAccountAccess.Owner));

        var accountB = Account(
            "AccountB",
            "30000000-0000-0000-0000-000000000002",
            SpaceB,
            Grant(UserB, TestAccountAccess.Owner));

        return new SecurityTopology(
            UserA,
            UserB,
            UserC,
            SpaceA,
            SpaceB,
            [
                new SecurityMembership(SpaceA.Id, UserA.Id, TestSpaceRole.Owner),
                new SecurityMembership(SpaceB.Id, UserB.Id, TestSpaceRole.Owner)
            ],
            [accountA, accountB],
            CreateSpaceAResources(accountA));
    }

    /// <summary>
    /// Sharing fixture. UserA and UserB are both members of SpaceA so account-level
    /// owner/viewer behavior can be tested independently from FullWorthSpace membership.
    /// </summary>
    public static SecurityTopology CreateSharingTopology()
    {
        var accountA = Account(
            "AccountA",
            "31000000-0000-0000-0000-000000000001",
            SpaceA,
            Grant(UserA, TestAccountAccess.Owner));

        var accountShared = Account(
            "AccountShared",
            "31000000-0000-0000-0000-000000000002",
            SpaceA,
            Grant(UserA, TestAccountAccess.Owner),
            Grant(UserB, TestAccountAccess.Owner));

        var accountViewOnly = Account(
            "AccountViewOnly",
            "31000000-0000-0000-0000-000000000003",
            SpaceA,
            Grant(UserA, TestAccountAccess.Owner),
            Grant(UserB, TestAccountAccess.Viewer));

        var accountB = Account(
            "AccountB",
            "31000000-0000-0000-0000-000000000004",
            SpaceB,
            Grant(UserB, TestAccountAccess.Owner));

        return new SecurityTopology(
            UserA,
            UserB,
            UserC,
            SpaceA,
            SpaceB,
            [
                new SecurityMembership(SpaceA.Id, UserA.Id, TestSpaceRole.Owner),
                new SecurityMembership(SpaceA.Id, UserB.Id, TestSpaceRole.Member),
                new SecurityMembership(SpaceB.Id, UserB.Id, TestSpaceRole.Owner)
            ],
            [accountA, accountShared, accountViewOnly, accountB],
            CreateSpaceAResources(accountA));
    }

    private static SecurityAccountRef Account(
        string name,
        string id,
        SecuritySpaceRef space,
        params SecurityAccountGrant[] grants) =>
        new(Guid.Parse(id), space.Id, name, grants);

    private static SecurityAccountGrant Grant(SecurityUserRef user, TestAccountAccess access) =>
        new(user.Id, access);

    private static IReadOnlyList<SecurityResourceRef> CreateSpaceAResources(SecurityAccountRef accountA)
    {
        var purchaseId = Guid.Parse("40000000-0000-0000-0000-000000000004");
        return
        [
            new(Guid.Parse("40000000-0000-0000-0000-000000000001"), "BankConnection", SpaceA.Id),
            new(Guid.Parse("40000000-0000-0000-0000-000000000002"), "BalanceSnapshot", SpaceA.Id, accountA.Id),
            new(Guid.Parse("40000000-0000-0000-0000-000000000003"), "FinanceTransaction", SpaceA.Id, accountA.Id),
            new(purchaseId, "Purchase", SpaceA.Id, accountA.Id),
            new(Guid.Parse("40000000-0000-0000-0000-000000000005"), "PurchaseItem", SpaceA.Id, accountA.Id, purchaseId),
            new(Guid.Parse("40000000-0000-0000-0000-000000000006"), "FinanceCategory", SpaceA.Id),
            new(Guid.Parse("40000000-0000-0000-0000-000000000007"), "CategorizationRule", SpaceA.Id),
            new(Guid.Parse("40000000-0000-0000-0000-000000000008"), "RecurringContract", SpaceA.Id),
            new(Guid.Parse("40000000-0000-0000-0000-000000000009"), "Budget", SpaceA.Id),
            new(Guid.Parse("40000000-0000-0000-0000-000000000010"), "Asset", SpaceA.Id),
            new(Guid.Parse("40000000-0000-0000-0000-000000000011"), "Liability", SpaceA.Id),
            new(Guid.Parse("40000000-0000-0000-0000-000000000012"), "NetWorthSnapshot", SpaceA.Id)
        ];
    }
}
