namespace FullWorth.Backend.Tests.Security;

public enum SecurityExpectedOutcome
{
    NotFound,
    Forbidden,
    ExcludesPrivateData,
    ExcludesForeignCandidates,
    RejectsCrossSpaceLink
}

public sealed record SecurityScenarioDefinition(
    string Key,
    string Resource,
    SecurityUserRef Caller,
    Guid TargetId,
    SecurityExpectedOutcome Expected,
    string PendingIntegrationAssertion);

public static class SecurityScenarioBuilder
{
    /// <summary>
    /// Security contracts for later executable integration tests.
    /// These definitions intentionally do not claim that current production authorization exists.
    /// </summary>
    public static IReadOnlyList<SecurityScenarioDefinition> CreateRequiredIdorBolaScenarios()
    {
        var isolation = MultiUserTestData.CreateIsolationTopology();
        var sharing = MultiUserTestData.CreateSharingTopology();
        var accountA = isolation.Account("AccountA");

        return
        [
            new(
                "space-foreign-uuid",
                "FullWorthSpace",
                isolation.UserB,
                isolation.SpaceA.Id,
                SecurityExpectedOutcome.NotFound,
                "GET SpaceA as unrelated UserB returns 404/no resource."),

            new(
                "account-private-uuid",
                "FinanceAccount",
                isolation.UserB,
                accountA.Id,
                SecurityExpectedOutcome.NotFound,
                "GET AccountA as unrelated UserB returns 404/no resource."),

            ForeignResource(isolation, "balance-private-account", "BalanceSnapshot"),
            ForeignResource(isolation, "transaction-private-account", "FinanceTransaction"),
            ForeignResource(isolation, "purchase-private-or-receipt", "Purchase"),
            ForeignResource(isolation, "purchase-item-private", "PurchaseItem"),
            ForeignResource(isolation, "category-foreign-uuid", "FinanceCategory"),
            ForeignResource(isolation, "rule-foreign-uuid", "CategorizationRule"),
            ForeignResource(isolation, "contract-foreign-uuid", "RecurringContract"),
            ForeignResource(isolation, "budget-foreign-uuid", "Budget"),
            ForeignResource(isolation, "asset-foreign-uuid", "Asset"),
            ForeignResource(isolation, "liability-foreign-uuid", "Liability"),
            ForeignResource(isolation, "net-worth-foreign-snapshot", "NetWorthSnapshot"),
            ForeignResource(isolation, "bank-connection-foreign-uuid", "BankConnection"),

            new(
                "analytics-private-account-aggregate",
                "Analytics",
                isolation.UserB,
                isolation.SpaceA.Id,
                SecurityExpectedOutcome.ExcludesPrivateData,
                "Analytics/dashboard/budget/forecast for UserB must not contain or infer AccountA values."),

            new(
                "export-private-resources",
                "Export",
                isolation.UserB,
                isolation.SpaceA.Id,
                SecurityExpectedOutcome.ExcludesPrivateData,
                "Export for UserB must not contain SpaceA/UserA private resources or provider internals."),

            new(
                "purchase-match-private-transaction",
                "PurchaseMatching",
                isolation.UserB,
                isolation.Resource("Purchase").Id,
                SecurityExpectedOutcome.ExcludesForeignCandidates,
                "Purchase match candidates must be generated only from transactions UserB may access."),

            new(
                "contract-detection-private-transaction",
                "ContractDetection",
                isolation.UserB,
                isolation.SpaceA.Id,
                SecurityExpectedOutcome.ExcludesForeignCandidates,
                "Contract detection must scope transactions before detection and reveal no private candidate evidence."),

            new(
                "viewer-owner-only-mutation",
                "FinanceAccount",
                sharing.UserB,
                sharing.Account("AccountViewOnly").Id,
                SecurityExpectedOutcome.Forbidden,
                "UserB can see AccountViewOnly but owner-only mutation returns 403."),

            new(
                "purchase-foreign-transaction-link",
                "CrossSpaceRelationship",
                isolation.UserB,
                isolation.Resource("Purchase").Id,
                SecurityExpectedOutcome.RejectsCrossSpaceLink,
                "Purchase/transaction and all other cross-space references are rejected without disclosing foreign resource details.")
        ];
    }

    private static SecurityScenarioDefinition ForeignResource(
        SecurityTopology topology,
        string key,
        string resourceType)
    {
        var resource = topology.Resource(resourceType);
        return new SecurityScenarioDefinition(
            key,
            resourceType,
            topology.UserB,
            resource.Id,
            SecurityExpectedOutcome.NotFound,
            $"Foreign {resourceType} UUID is indistinguishable from nonexistent and returns 404/no resource.");
    }
}
