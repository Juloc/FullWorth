namespace FullWorth.Backend.Modules.Portfolio;

public sealed record PropertyUnitWrite(
    string Name,
    string UnitType,
    decimal? AreaSqm,
    decimal? Rooms,
    decimal? OwnershipSharePercent,
    bool IsOwnerOccupied,
    bool IsActive,
    string? Notes);

public sealed record PropertyUnitView(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid AssetId,
    string Name,
    string UnitType,
    decimal? AreaSqm,
    decimal? Rooms,
    decimal? OwnershipSharePercent,
    bool IsOwnerOccupied,
    bool IsActive,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RentalLeaseWrite(
    Guid PropertyUnitId,
    string? TenantDisplayLabel,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Status,
    decimal ColdRent,
    decimal? UtilitiesAdvance,
    decimal? OtherRecurringCharges,
    string Currency,
    string PaymentCycle,
    decimal? DepositAmount,
    bool? DepositHeld,
    DateOnly? LastRentChangeDate,
    DateOnly? NextReviewDate,
    string? Notes);

public sealed record RentalLeaseView(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid AssetId,
    Guid PropertyUnitId,
    string UnitName,
    string? TenantDisplayLabel,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Status,
    decimal ColdRent,
    decimal? UtilitiesAdvance,
    decimal? OtherRecurringCharges,
    decimal WarmRent,
    string Currency,
    string PaymentCycle,
    decimal? DepositAmount,
    bool? DepositHeld,
    DateOnly? LastRentChangeDate,
    DateOnly? NextReviewDate,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AssetCashflowWrite(
    Guid? TransactionId,
    DateOnly? Date,
    string Type,
    decimal Amount,
    string Direction,
    string? Currency,
    bool IsPlanned,
    string? Notes);

public sealed record AssetCashflowView(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid AssetId,
    Guid? TransactionId,
    DateOnly Date,
    string Type,
    decimal Amount,
    string Direction,
    string Currency,
    bool IsPlanned,
    string? Notes,
    string? TransactionCounterparty,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PropertyImprovementWrite(
    string Title,
    string Category,
    DateOnly? StartDate,
    DateOnly? CompletedDate,
    decimal? Cost,
    string? Currency,
    decimal? EstimatedValueAdded,
    string? Description,
    Guid? DocumentId);

public sealed record PropertyImprovementView(
    Guid Id,
    Guid AssetId,
    string Title,
    string Category,
    DateOnly? StartDate,
    DateOnly? CompletedDate,
    decimal? Cost,
    string? Currency,
    decimal? EstimatedValueAdded,
    string? Description,
    Guid? DocumentId,
    IReadOnlyList<Guid> CashflowEntryIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ImprovementCashflowLinkWrite(Guid CashflowEntryId);

public sealed record AssetRecurringContractLinkWrite(Guid RecurringContractId, string Role);

public sealed record AssetRecurringContractLinkView(
    Guid AssetId,
    Guid RecurringContractId,
    string Role,
    string ContractName,
    decimal Amount,
    string Currency,
    string BillingCycle,
    bool IsActive,
    DateOnly? NextDueDate,
    DateTimeOffset CreatedAt);

public static class RealEstateOperationsKinds
{
    public static readonly IReadOnlySet<string> UnitTypes = new HashSet<string>(StringComparer.Ordinal)
    { "apartment", "commercial", "parking", "storage", "other" };

    public static readonly IReadOnlySet<string> LeaseStatuses = new HashSet<string>(StringComparer.Ordinal)
    { "planned", "active", "ended" };

    public static readonly IReadOnlySet<string> PaymentCycles = new HashSet<string>(StringComparer.Ordinal)
    { "weekly", "monthly", "quarterly", "yearly" };

    public static readonly IReadOnlySet<string> CashflowTypes = new HashSet<string>(StringComparer.Ordinal)
    { "rental_income", "income", "operating_expense", "capex", "debt_payment", "tax", "insurance", "fee", "distribution", "other" };

    public static readonly IReadOnlySet<string> Directions = new HashSet<string>(StringComparer.Ordinal)
    { "income", "expense" };

    public static readonly IReadOnlySet<string> ImprovementCategories = new HashSet<string>(StringComparer.Ordinal)
    { "windows", "roof", "heating", "insulation", "electrical", "plumbing", "bathroom", "kitchen", "flooring", "facade", "solar", "structural", "other" };

    public static readonly IReadOnlySet<string> ContractRoles = new HashSet<string>(StringComparer.Ordinal)
    { "hoa", "property_tax", "insurance", "utilities", "maintenance_plan", "other" };
}
