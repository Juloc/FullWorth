namespace FullWorth.Backend.Modules.Portfolio;

public enum SpecializedAssetMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid,
    Conflict
}

public sealed record SpecializedAssetOutcome<T>(
    SpecializedAssetMutationResult Result,
    T? Value = default,
    string? Error = null);

public sealed record VehicleDetailWrite(
    string VehicleType,
    string? Manufacturer,
    string? Model,
    string? Variant,
    string? Vin,
    string? LicensePlate,
    DateOnly? FirstRegistrationDate,
    int? ModelYear,
    int? MileageKm,
    string? Powertrain,
    decimal? PowerKw,
    DateOnly? PurchaseDate,
    decimal? PurchasePrice,
    string? PurchaseCurrency,
    string? Condition,
    int? AnnualMileageEstimate,
    string? Notes);

public sealed record VehicleDetailView(
    Guid AssetId,
    string VehicleType,
    string? Manufacturer,
    string? Model,
    string? Variant,
    string? Vin,
    string? LicensePlate,
    DateOnly? FirstRegistrationDate,
    int? ModelYear,
    int? MileageKm,
    string? Powertrain,
    decimal? PowerKw,
    DateOnly? PurchaseDate,
    decimal? PurchasePrice,
    string? PurchaseCurrency,
    string? Condition,
    int? AnnualMileageEstimate,
    string? Notes,
    DateTimeOffset UpdatedAt);

public sealed record VehicleEstimateWrite(
    decimal AnnualDepreciationPercent,
    decimal MileageAdjustmentPercent = 0m,
    decimal ConditionAdjustmentPercent = 0m,
    decimal RangePercent = 10m);

public sealed record PreciousMetalDetailWrite(
    string MetalType,
    string Form,
    decimal Quantity,
    decimal? GrossWeightGrams,
    decimal? Purity,
    string? StorageLabel,
    DateOnly? PurchaseDate,
    decimal? PurchasePrice,
    string? PurchaseCurrency,
    string? Notes);

public sealed record PreciousMetalDetailView(
    Guid AssetId,
    string MetalType,
    string Form,
    decimal Quantity,
    decimal? GrossWeightGrams,
    decimal? Purity,
    decimal? FineWeightGrams,
    string? StorageLabel,
    DateOnly? PurchaseDate,
    decimal? PurchasePrice,
    string? PurchaseCurrency,
    string? Notes,
    DateTimeOffset UpdatedAt);

public sealed record PreciousMetalEstimateWrite(
    decimal ReferencePricePerFineGram,
    string Currency,
    decimal PremiumAdjustmentPercent = 0m,
    decimal RangePercent = 5m);

public sealed record SpecializedAssetEstimateView(
    decimal Amount,
    decimal LowEstimate,
    decimal HighEstimate,
    string Currency,
    DateOnly ValuedAt,
    string Method,
    IReadOnlyDictionary<string, object?> Inputs,
    IReadOnlyList<string> Assumptions);
