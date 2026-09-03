namespace FullWorth.Backend.Modules.Portfolio;

public sealed record PropertyEnergyCertificateWrite(
    string CertificateType,
    string? EnergyClass,
    decimal? EnergyValueKwhSqmYear,
    string? PrimaryEnergySource,
    DateOnly? IssuedAt,
    DateOnly? ValidUntil,
    int? BuildingYearOnCertificate,
    Guid? DocumentId,
    bool IsCurrent,
    string? Notes);

public sealed record PropertyEnergyCertificateView(
    Guid Id,
    Guid AssetId,
    string CertificateType,
    string? EnergyClass,
    decimal? EnergyValueKwhSqmYear,
    string? PrimaryEnergySource,
    DateOnly? IssuedAt,
    DateOnly? ValidUntil,
    int? BuildingYearOnCertificate,
    Guid? DocumentId,
    bool IsCurrent,
    bool IsExpired,
    bool ExpiresSoon,
    string? Notes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AssetDocumentView(
    Guid Id,
    Guid AssetId,
    string Category,
    string OriginalFileName,
    string MediaType,
    long SizeBytes,
    string? Notes,
    DateTimeOffset CreatedAt);

public sealed record AssetDocumentFile(string AbsolutePath, string MediaType, string FileName);

public sealed record InternalPropertyEstimateWrite(
    decimal ReferencePricePerSqm,
    decimal ConditionAdjustmentPercent = 0m,
    decimal ModernizationAdjustmentPercent = 0m,
    decimal FeatureAdjustmentPercent = 0m,
    decimal RangePercent = 10m);

public sealed record PropertyEstimateView(
    decimal Amount,
    decimal LowEstimate,
    decimal HighEstimate,
    string Currency,
    DateOnly ValuedAt,
    string Method,
    string Source,
    IReadOnlyDictionary<string, object?> Inputs,
    IReadOnlyList<string> Assumptions);

public sealed record PropertyValuationCapabilityView(
    bool ManualAvailable,
    bool InternalEstimateAvailable,
    IReadOnlyList<PropertyValuationProviderCapability> ExternalProviders);

public sealed record PropertyValuationProviderCapability(string Key, string DisplayName);

public sealed record ExternalPropertyValuationWrite(string ProviderKey);

public sealed record PropertyValuationRequest(
    Guid AssetId,
    string CountryCode,
    string? PostalCode,
    string? City,
    string? Street,
    string? HouseNumber,
    string PropertyType,
    decimal? LivingAreaSqm,
    int? YearBuilt,
    string? Condition,
    string Currency);

public sealed record PropertyValuationResult(
    decimal Amount,
    decimal? LowEstimate,
    decimal? HighEstimate,
    decimal? Confidence,
    string Currency,
    DateOnly ValuedAt,
    string ProviderKey,
    string ProviderDisplayName,
    string? ExternalReference,
    IReadOnlyDictionary<string, object?>? InputSummary = null);

public interface IPropertyValuationProvider
{
    string Key { get; }
    string DisplayName { get; }
    Task<PropertyValuationResult> EstimateAsync(PropertyValuationRequest request, CancellationToken ct);
}
