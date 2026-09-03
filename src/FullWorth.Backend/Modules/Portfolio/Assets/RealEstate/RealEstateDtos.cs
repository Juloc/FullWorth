namespace FullWorth.Backend.Modules.Portfolio;

public class RealEstateDetailWrite
{
    public string PropertyType { get; set; } = "apartment";
    public string UsageType { get; set; } = "owner_occupied";
    public string CountryCode { get; set; } = "DE";
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Street { get; set; }
    public string? HouseNumber { get; set; }
    public string? AddressExtra { get; set; }
    public string? UnitLabel { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public int? YearBuilt { get; set; }
    public int? LastMajorModernizationYear { get; set; }
    public decimal? LivingAreaSqm { get; set; }
    public decimal? UsableAreaSqm { get; set; }
    public decimal? PlotAreaSqm { get; set; }
    public decimal? Rooms { get; set; }
    public int? Bedrooms { get; set; }
    public int? Bathrooms { get; set; }
    public int? Floor { get; set; }
    public int? TotalFloors { get; set; }
    public decimal OwnershipSharePercent { get; set; } = 100m;
    public int? ParkingSpaces { get; set; }
    public int? GarageSpaces { get; set; }
    public string? Condition { get; set; }
    public string? ConstructionType { get; set; }
    public string? HeatingType { get; set; }
    public string? PrimaryEnergySource { get; set; }
    public bool? Elevator { get; set; }
    public bool? BarrierFree { get; set; }
    public bool? BalconyTerrace { get; set; }
    public bool? Basement { get; set; }
    public bool? Garden { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public decimal? PurchasePrice { get; set; }
    public string? PurchaseCurrency { get; set; }
    public decimal? AcquisitionCosts { get; set; }
    public decimal? EquityAtPurchase { get; set; }
    public string? Notes { get; set; }
}

public sealed class RealEstateDetailView : RealEstateDetailWrite
{
    public Guid AssetId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record RealEstatePropertyView(
    Guid AssetId,
    string Name,
    decimal CurrentValue,
    string Currency,
    DateOnly? ValuedAt,
    bool IncludeInNetWorth,
    RealEstateDetailView? Detail);

public sealed record RealEstateAcquisitionCostWrite(
    string Type,
    decimal Amount,
    string Currency,
    DateOnly? Date,
    string? Notes);

public sealed record RealEstateAcquisitionCostView(
    Guid Id,
    Guid AssetId,
    string Type,
    decimal Amount,
    string Currency,
    DateOnly? Date,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AssetDebtLinkWrite(
    Guid? LoanId,
    Guid? LiabilityId,
    string RelationType,
    decimal AllocationPercent = 100m);

public sealed record AssetDebtLinkView(
    Guid Id,
    Guid AssetId,
    Guid? LoanId,
    Guid? LiabilityId,
    string RelationType,
    decimal AllocationPercent,
    DateTimeOffset CreatedAt,
    string DebtType,
    string Name,
    decimal CurrentBalance,
    string Currency,
    decimal? OriginalPrincipal,
    decimal? InterestRate,
    decimal? RegularPayment,
    DateOnly? StartDate,
    DateOnly? EndDate);

public sealed record RealEstateMetricsView(
    Guid AssetId,
    decimal CurrentValue,
    string Currency,
    decimal AllocatedDebt,
    decimal? Equity,
    decimal? Ltv,
    decimal? AcquisitionBasis,
    decimal? ValueGain,
    decimal OwnershipSharePercent,
    bool IsComplete,
    IReadOnlyList<string> MissingCurrencies)
{
    public DateOnly? MetricsFrom { get; init; }
    public DateOnly? MetricsTo { get; init; }
    public decimal? AnnualColdRent { get; init; }
    public decimal? ActualRent { get; init; }
    public decimal? NonRecoverableOperatingCosts { get; init; }
    public decimal? NetOperatingIncome { get; init; }
    public decimal? GrossYield { get; init; }
    public decimal? NetRentalYield { get; init; }
    public decimal? DebtPayments { get; init; }
    public decimal? CashflowBeforeTax { get; init; }
}

public enum RealEstateMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public sealed record RealEstateMutationOutcome<T>(
    RealEstateMutationResult Result,
    T? Value = default,
    string? Error = null);
