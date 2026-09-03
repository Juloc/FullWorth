namespace FullWorth.Backend.Tests.Security;

/// <summary>
/// These tests validate only the reusable B5 security fixtures/contracts.
/// They do not exercise production authorization and must not be interpreted as an IDOR/BOLA pass.
/// Integrator B/later authorization work must turn the pending assertions into executable integration tests.
/// </summary>
public sealed class SecurityScenarioDefinitionTests
{
    [Fact]
    public void IsolationTopologyKeepsUserBOutsideSpaceA()
    {
        var topology = MultiUserTestData.CreateIsolationTopology();

        Assert.True(topology.IsMember(topology.UserA, topology.SpaceA));
        Assert.False(topology.IsMember(topology.UserB, topology.SpaceA));
        Assert.True(topology.IsMember(topology.UserB, topology.SpaceB));
        Assert.Null(topology.AccountAccess(topology.UserB, topology.Account("AccountA")));
    }

    [Fact]
    public void SharingTopologyRepresentsOwnerSharedViewerAndPrivateAccounts()
    {
        var topology = MultiUserTestData.CreateSharingTopology();

        Assert.True(topology.IsMember(topology.UserA, topology.SpaceA));
        Assert.True(topology.IsMember(topology.UserB, topology.SpaceA));

        Assert.Equal(TestAccountAccess.Owner, topology.AccountAccess(topology.UserA, topology.Account("AccountA")));
        Assert.Null(topology.AccountAccess(topology.UserB, topology.Account("AccountA")));

        Assert.Equal(TestAccountAccess.Owner, topology.AccountAccess(topology.UserA, topology.Account("AccountShared")));
        Assert.Equal(TestAccountAccess.Owner, topology.AccountAccess(topology.UserB, topology.Account("AccountShared")));

        Assert.Equal(TestAccountAccess.Owner, topology.AccountAccess(topology.UserA, topology.Account("AccountViewOnly")));
        Assert.Equal(TestAccountAccess.Viewer, topology.AccountAccess(topology.UserB, topology.Account("AccountViewOnly")));

        Assert.Equal(TestAccountAccess.Owner, topology.AccountAccess(topology.UserB, topology.Account("AccountB")));
        Assert.Equal(topology.SpaceB.Id, topology.Account("AccountB").FullWorthSpaceId);
    }

    [Fact]
    public void ScenarioListCoversRequiredProtectedResourcesAndAggregates()
    {
        var scenarios = SecurityScenarioBuilder.CreateRequiredIdorBolaScenarios();
        var resources = scenarios.Select(x => x.Resource).ToHashSet(StringComparer.Ordinal);

        string[] required =
        [
            "FullWorthSpace",
            "FinanceAccount",
            "BalanceSnapshot",
            "FinanceTransaction",
            "Purchase",
            "PurchaseItem",
            "FinanceCategory",
            "CategorizationRule",
            "RecurringContract",
            "Budget",
            "Asset",
            "Liability",
            "NetWorthSnapshot",
            "BankConnection",
            "Analytics",
            "Export",
            "PurchaseMatching",
            "ContractDetection",
            "CrossSpaceRelationship"
        ];

        foreach (var resource in required)
            Assert.Contains(resource, resources);
    }

    [Fact]
    public void ForeignUuidScenariosUseNotFoundContract()
    {
        var scenarios = SecurityScenarioBuilder.CreateRequiredIdorBolaScenarios();
        string[] notFoundKeys =
        [
            "space-foreign-uuid",
            "account-private-uuid",
            "balance-private-account",
            "transaction-private-account",
            "purchase-private-or-receipt",
            "purchase-item-private",
            "category-foreign-uuid",
            "rule-foreign-uuid",
            "contract-foreign-uuid",
            "budget-foreign-uuid",
            "asset-foreign-uuid",
            "liability-foreign-uuid",
            "net-worth-foreign-snapshot",
            "bank-connection-foreign-uuid"
        ];

        foreach (var key in notFoundKeys)
            Assert.Equal(SecurityExpectedOutcome.NotFound, scenarios.Single(x => x.Key == key).Expected);
    }

    [Fact]
    public void ViewerMutationUsesForbiddenOnlyAfterVisibilityIsEstablished()
    {
        var scenario = SecurityScenarioBuilder.CreateRequiredIdorBolaScenarios()
            .Single(x => x.Key == "viewer-owner-only-mutation");
        var topology = MultiUserTestData.CreateSharingTopology();
        var account = topology.Account("AccountViewOnly");

        Assert.True(topology.IsMember(topology.UserB, topology.SpaceA));
        Assert.Equal(TestAccountAccess.Viewer, topology.AccountAccess(topology.UserB, account));
        Assert.Equal(SecurityExpectedOutcome.Forbidden, scenario.Expected);
    }

    [Fact]
    public void AggregateAndExportScenariosExplicitlyExcludePrivateData()
    {
        var scenarios = SecurityScenarioBuilder.CreateRequiredIdorBolaScenarios();

        Assert.Equal(
            SecurityExpectedOutcome.ExcludesPrivateData,
            scenarios.Single(x => x.Key == "analytics-private-account-aggregate").Expected);
        Assert.Equal(
            SecurityExpectedOutcome.ExcludesPrivateData,
            scenarios.Single(x => x.Key == "export-private-resources").Expected);
    }
}
