namespace FullWorth.Backend.Modules.Portfolio;

internal static class RealEstateValidation
{
    internal static readonly IReadOnlySet<string> PropertyTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "apartment", "detached_house", "semi_detached", "row_house", "multi_family", "land", "commercial", "mixed", "other"
    };

    internal static readonly IReadOnlySet<string> UsageTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "owner_occupied", "rented", "mixed", "vacant"
    };

    internal static readonly IReadOnlySet<string> Conditions = new HashSet<string>(StringComparer.Ordinal)
    {
        "new", "renovated", "good", "needs_renovation", "major_renovation", "unknown"
    };

    internal static readonly IReadOnlySet<string> CostTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "property_price", "transfer_tax", "notary", "land_registry", "broker", "renovation_at_purchase", "financing_fee", "other"
    };

    internal static readonly IReadOnlySet<string> RelationTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "mortgage", "vehicle_finance", "secured_loan", "other"
    };

    internal static string? Validate(RealEstateDetailWrite request)
    {
        var propertyType = request.PropertyType?.Trim().ToLowerInvariant() ?? string.Empty;
        var usageType = request.UsageType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!PropertyTypes.Contains(propertyType)) return "Unsupported property type.";
        if (!UsageTypes.Contains(usageType)) return "Unsupported usage type.";

        var country = request.CountryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (country.Length != 2 || country.Any(c => c is < 'A' or > 'Z')) return "Country code must contain two letters.";
        if (request.PurchaseCurrency is { } purchaseCurrency && !ValidCurrency(purchaseCurrency)) return "Purchase currency must contain three letters.";
        if (request.OwnershipSharePercent <= 0m || request.OwnershipSharePercent > 100m) return "Ownership share must be greater than zero and at most 100 percent.";
        if (request.Latitude is < -90m or > 90m || request.Longitude is < -180m or > 180m) return "Coordinates are outside the valid range.";
        if (request.YearBuilt is < 1000 or > 3000 || request.LastMajorModernizationYear is < 1000 or > 3000) return "Building years must be between 1000 and 3000.";
        if (request.LivingAreaSqm is < 0m || request.UsableAreaSqm is < 0m || request.PlotAreaSqm is < 0m || request.Rooms is < 0m) return "Areas and room counts cannot be negative.";
        if (request.Bedrooms is < 0 || request.Bathrooms is < 0 || request.TotalFloors is < 0 || request.ParkingSpaces is < 0 || request.GarageSpaces is < 0) return "Counts cannot be negative.";
        if (request.PurchasePrice is < 0m || request.AcquisitionCosts is < 0m || request.EquityAtPurchase is < 0m) return "Purchase values cannot be negative.";
        var condition = Trim(request.Condition)?.ToLowerInvariant();
        return condition is not null && !Conditions.Contains(condition) ? "Unsupported property condition." : null;
    }

    internal static string? Validate(RealEstateAcquisitionCostWrite request)
    {
        if (!CostTypes.Contains(request.Type?.Trim().ToLowerInvariant() ?? string.Empty)) return "Unsupported acquisition cost type.";
        if (request.Amount < 0m) return "Acquisition cost cannot be negative.";
        return ValidCurrency(request.Currency) ? null : "Currency must contain three letters.";
    }

    internal static void Normalize(RealEstateDetailWrite request)
    {
        request.PropertyType = request.PropertyType.Trim().ToLowerInvariant();
        request.UsageType = request.UsageType.Trim().ToLowerInvariant();
        request.CountryCode = request.CountryCode.Trim().ToUpperInvariant();
        request.PostalCode = Trim(request.PostalCode);
        request.City = Trim(request.City);
        request.Street = Trim(request.Street);
        request.HouseNumber = Trim(request.HouseNumber);
        request.AddressExtra = Trim(request.AddressExtra);
        request.UnitLabel = Trim(request.UnitLabel);
        request.Condition = Trim(request.Condition)?.ToLowerInvariant();
        request.ConstructionType = Trim(request.ConstructionType);
        request.HeatingType = Trim(request.HeatingType);
        request.PrimaryEnergySource = Trim(request.PrimaryEnergySource);
        request.PurchaseCurrency = Trim(request.PurchaseCurrency)?.ToUpperInvariant();
        request.Notes = Trim(request.Notes);
    }

    internal static bool ValidCurrency(string? value)
    {
        var currency = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return currency.Length == 3 && currency.All(c => c is >= 'A' and <= 'Z');
    }

    internal static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
