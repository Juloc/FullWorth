using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class PropertyValuationProviderRegistry(IEnumerable<IPropertyValuationProvider> providers)
{
    private readonly IReadOnlyDictionary<string, IPropertyValuationProvider> map = providers
        .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PropertyValuationProviderCapability> Capabilities => map.Values
        .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(x => new PropertyValuationProviderCapability(x.Key, x.DisplayName)).ToArray();

    public IPropertyValuationProvider? Find(string? key) =>
        !string.IsNullOrWhiteSpace(key) && map.TryGetValue(key.Trim(), out var provider) ? provider : null;
}

public sealed class PropertyValuationService(
    FullWorthDbContext db,
    AssetValuationStore valuations,
    PropertyValuationProviderRegistry providers,
    AuditService audit)
{
    public async Task<RealEstateMutationOutcome<PropertyValuationCapabilityView>> GetCapabilitiesAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadPropertyAsync(userId, fullWorthSpaceId, assetId, ct)) return new(RealEstateMutationResult.NotFound);
        var detail = await ReadPropertyAsync(fullWorthSpaceId, assetId, ct);
        return new(RealEstateMutationResult.Success,
            new PropertyValuationCapabilityView(true, detail?.LivingAreaSqm is > 0m, providers.Capabilities));
    }

    public async Task<RealEstateMutationOutcome<PropertyEstimateView>> EstimateInternalAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, InternalPropertyEstimateWrite request, CancellationToken ct)
    {
        var role = await RoleAsync(userId, fullWorthSpaceId, assetId, ct);
        if (role is null) return new(RealEstateMutationResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(RealEstateMutationResult.Forbidden);
        if (request.ReferencePricePerSqm <= 0m) return new(RealEstateMutationResult.Invalid, Error: "Reference price per square metre must be greater than zero.");
        if (request.RangePercent is < 0m or > 50m) return new(RealEstateMutationResult.Invalid, Error: "Range percent must be between 0 and 50.");
        foreach (var adjustment in new[] { request.ConditionAdjustmentPercent, request.ModernizationAdjustmentPercent, request.FeatureAdjustmentPercent })
            if (adjustment is < -50m or > 50m) return new(RealEstateMutationResult.Invalid, Error: "Each adjustment must be between -50 and 50 percent.");

        var detail = await ReadPropertyAsync(fullWorthSpaceId, assetId, ct);
        if (detail is null || detail.LivingAreaSqm is not > 0m) return new(RealEstateMutationResult.Invalid, Error: "Living area is required for the internal estimate.");
        var baseAmount = detail.LivingAreaSqm.Value * request.ReferencePricePerSqm;
        var adjustmentPercent = request.ConditionAdjustmentPercent + request.ModernizationAdjustmentPercent + request.FeatureAdjustmentPercent;
        var amount = Math.Max(0m, Math.Round(baseAmount * (1m + adjustmentPercent / 100m), 2));
        var range = request.RangePercent / 100m;
        var low = Math.Round(amount * (1m - range), 2);
        var high = Math.Round(amount * (1m + range), 2);
        var inputs = new Dictionary<string, object?>
        {
            ["livingAreaSqm"] = detail.LivingAreaSqm,
            ["referencePricePerSqm"] = request.ReferencePricePerSqm,
            ["conditionAdjustmentPercent"] = request.ConditionAdjustmentPercent,
            ["modernizationAdjustmentPercent"] = request.ModernizationAdjustmentPercent,
            ["featureAdjustmentPercent"] = request.FeatureAdjustmentPercent,
            ["totalAdjustmentPercent"] = adjustmentPercent,
            ["rangePercent"] = request.RangePercent
        };
        var assumptions = new[]
        {
            "Reference price per m² is supplied by the user; FullWorth does not infer regional market data.",
            "Adjustments are additive percentages chosen by the user.",
            "The estimate is informational until explicitly accepted as a valuation."
        };
        audit.Record(fullWorthSpaceId, userId, "action.internal_property_valuation.calculated", "Asset", assetId);
        await db.SaveChangesAsync(ct);
        return new(RealEstateMutationResult.Success,
            new PropertyEstimateView(amount, low, high, detail.Currency, DateOnly.FromDateTime(DateTime.UtcNow), "internal_estimate", "FullWorth deterministic estimator", inputs, assumptions));
    }

    public async Task<RealEstateMutationOutcome<AssetValuationView>> EstimateExternalAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, ExternalPropertyValuationWrite request, CancellationToken ct)
    {
        var role = await RoleAsync(userId, fullWorthSpaceId, assetId, ct);
        if (role is null) return new(RealEstateMutationResult.NotFound);
        if (role != FullWorthSpaceRoles.Owner) return new(RealEstateMutationResult.Forbidden);
        var provider = providers.Find(request.ProviderKey);
        if (provider is null) return new(RealEstateMutationResult.Invalid, Error: "Property valuation provider is not configured.");
        var detail = await ReadPropertyAsync(fullWorthSpaceId, assetId, ct);
        if (detail is null) return new(RealEstateMutationResult.NotFound);
        var providerRequest = new PropertyValuationRequest(assetId, detail.CountryCode, detail.PostalCode, detail.City, detail.Street, detail.HouseNumber, detail.PropertyType, detail.LivingAreaSqm, detail.YearBuilt, detail.Condition, detail.Currency);

        audit.Record(fullWorthSpaceId, userId, "action.external_property_valuation.requested", "Asset", assetId);
        await db.SaveChangesAsync(ct);
        PropertyValuationResult result;
        try { result = await provider.EstimateAsync(providerRequest, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return new(RealEstateMutationResult.Invalid, Error: "External property valuation failed. The accepted value was not changed."); }

        var stored = await valuations.CreateForUserAsync(userId, fullWorthSpaceId, assetId,
            new AssetValuationWrite(result.Amount, result.Currency, result.ValuedAt, "external_provider", result.LowEstimate, result.HighEstimate, result.Confidence,
                result.ProviderKey, result.ProviderDisplayName, result.ExternalReference, IsAccepted: false), ct);
        if (stored.Result != AssetValuationMutationResult.Success || stored.Valuation is null)
            return new(RealEstateMutationResult.Invalid, Error: stored.Error ?? "External valuation could not be stored.");
        return new(RealEstateMutationResult.Success, stored.Valuation);
    }

    private async Task<string?> RoleAsync(Guid userId, Guid space, Guid asset, CancellationToken ct)
    {
        if (!await CanReadPropertyAsync(userId, space, asset, ct)) return null;
        return await db.FullWorthSpaceMembers.AsNoTracking().Where(x=>x.FullWorthSpaceId==space&&x.UserId==userId).Select(x=>x.Role).SingleOrDefaultAsync(ct);
    }

    private async Task<bool> CanReadPropertyAsync(Guid userId, Guid space, Guid asset, CancellationToken ct) =>
        await db.Assets.AsNoTracking().AnyAsync(x=>x.Id==asset&&x.FullWorthSpaceId==space&&x.Kind==AssetKinds.RealEstate&&db.FullWorthSpaceMembers.Any(m=>m.FullWorthSpaceId==space&&m.UserId==userId),ct);

    private async Task<PropertyProjection?> ReadPropertyAsync(Guid space, Guid asset, CancellationToken ct)
    {
        var currency = await db.Assets.AsNoTracking().Where(a=>a.Id==asset&&a.FullWorthSpaceId==space).Select(a=>a.Currency).SingleOrDefaultAsync(ct);
        if (currency is null) return null;
        var detail = await db.Database.SqlQuery<PropertyDetailProjection>($"""
SELECT "CountryCode","PostalCode","City","Street","HouseNumber","PropertyType","LivingAreaSqm","YearBuilt","Condition"
FROM "RealEstateAssetDetails" WHERE "AssetId"={asset}
""").SingleOrDefaultAsync(ct);
        return detail is null ? null : new PropertyProjection(currency, detail);
    }

    public sealed class PropertyDetailProjection
    {
        public string CountryCode { get; set; } = "DE"; public string? PostalCode { get; set; } public string? City { get; set; } public string? Street { get; set; }
        public string? HouseNumber { get; set; } public string PropertyType { get; set; } = "apartment"; public decimal? LivingAreaSqm { get; set; } public int? YearBuilt { get; set; } public string? Condition { get; set; }
    }
    private sealed record PropertyProjection(string Currency, PropertyDetailProjection Detail)
    {
        public string CountryCode => Detail.CountryCode; public string? PostalCode => Detail.PostalCode; public string? City => Detail.City; public string? Street => Detail.Street;
        public string? HouseNumber => Detail.HouseNumber; public string PropertyType => Detail.PropertyType; public decimal? LivingAreaSqm => Detail.LivingAreaSqm; public int? YearBuilt => Detail.YearBuilt; public string? Condition => Detail.Condition;
    }
}
