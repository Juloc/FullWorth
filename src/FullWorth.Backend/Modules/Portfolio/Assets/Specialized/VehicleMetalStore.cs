using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class VehicleMetalStore(FullWorthDbContext db, AuditService audit)
{
    private static readonly IReadOnlySet<string> VehicleTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "car", "motorcycle", "camper", "boat", "other"
    };

    private static readonly IReadOnlySet<string> Powertrains = new HashSet<string>(StringComparer.Ordinal)
    {
        "petrol", "diesel", "hybrid", "phev", "electric", "other"
    };

    private static readonly IReadOnlySet<string> MetalTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "gold", "silver", "platinum", "palladium", "other"
    };

    private static readonly IReadOnlySet<string> MetalForms = new HashSet<string>(StringComparer.Ordinal)
    {
        "bar", "coin", "jewelry", "other"
    };

    public async Task<SpecializedAssetOutcome<VehicleDetailView?>> GetVehicleAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadAsync(userId, fullWorthSpaceId, assetId, AssetKinds.Vehicle, ct))
            return new(SpecializedAssetMutationResult.NotFound);

        return new(SpecializedAssetMutationResult.Success, await ReadVehicleAsync(assetId, ct));
    }

    public async Task<SpecializedAssetOutcome<VehicleDetailView>> PutVehicleAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, VehicleDetailWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.Vehicle, ct);
        if (access != SpecializedAssetMutationResult.Success) return new(access);

        var error = ValidateVehicle(request);
        if (error is not null) return new(SpecializedAssetMutationResult.Invalid, Error: error);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "VehicleAssetDetails"
                ("AssetId", "VehicleType", "Manufacturer", "Model", "Variant", "VIN", "LicensePlate",
                 "FirstRegistrationDate", "ModelYear", "MileageKm", "Powertrain", "PowerKw", "PurchaseDate",
                 "PurchasePrice", "PurchaseCurrency", "Condition", "AnnualMileageEstimate", "Notes", "UpdatedAt")
            VALUES
                ({assetId}, {Normalize(request.VehicleType)}, {Trim(request.Manufacturer)}, {Trim(request.Model)},
                 {Trim(request.Variant)}, {Trim(request.Vin)}, {Trim(request.LicensePlate)}, {request.FirstRegistrationDate},
                 {request.ModelYear}, {request.MileageKm}, {NormalizeNullable(request.Powertrain)}, {request.PowerKw},
                 {request.PurchaseDate}, {request.PurchasePrice}, {NormalizeCurrency(request.PurchaseCurrency)},
                 {Trim(request.Condition)}, {request.AnnualMileageEstimate}, {Trim(request.Notes)}, now())
            ON CONFLICT ("AssetId") DO UPDATE SET
                "VehicleType" = EXCLUDED."VehicleType",
                "Manufacturer" = EXCLUDED."Manufacturer",
                "Model" = EXCLUDED."Model",
                "Variant" = EXCLUDED."Variant",
                "VIN" = EXCLUDED."VIN",
                "LicensePlate" = EXCLUDED."LicensePlate",
                "FirstRegistrationDate" = EXCLUDED."FirstRegistrationDate",
                "ModelYear" = EXCLUDED."ModelYear",
                "MileageKm" = EXCLUDED."MileageKm",
                "Powertrain" = EXCLUDED."Powertrain",
                "PowerKw" = EXCLUDED."PowerKw",
                "PurchaseDate" = EXCLUDED."PurchaseDate",
                "PurchasePrice" = EXCLUDED."PurchasePrice",
                "PurchaseCurrency" = EXCLUDED."PurchaseCurrency",
                "Condition" = EXCLUDED."Condition",
                "AnnualMileageEstimate" = EXCLUDED."AnnualMileageEstimate",
                "Notes" = EXCLUDED."Notes",
                "UpdatedAt" = now();
            """, ct);

        audit.Record(fullWorthSpaceId, userId, "asset.vehicle.updated", "Asset", assetId);
        await db.SaveChangesAsync(ct);
        var view = await ReadVehicleAsync(assetId, ct);
        return view is null
            ? new(SpecializedAssetMutationResult.NotFound)
            : new(SpecializedAssetMutationResult.Success, view);
    }

    public async Task<SpecializedAssetOutcome<SpecializedAssetEstimateView>> EstimateVehicleAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, VehicleEstimateWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.Vehicle, ct);
        if (access != SpecializedAssetMutationResult.Success) return new(access);

        if (request.AnnualDepreciationPercent is < 0m or > 80m)
            return new(SpecializedAssetMutationResult.Invalid, Error: "Annual depreciation must be between 0 and 80 percent.");
        if (request.MileageAdjustmentPercent is < -50m or > 50m || request.ConditionAdjustmentPercent is < -50m or > 50m)
            return new(SpecializedAssetMutationResult.Invalid, Error: "Vehicle adjustments must be between -50 and 50 percent.");
        if (request.RangePercent is < 0m or > 50m)
            return new(SpecializedAssetMutationResult.Invalid, Error: "Estimate range must be between 0 and 50 percent.");

        var detail = await ReadVehicleAsync(assetId, ct);
        if (detail?.PurchasePrice is not > 0m || detail.PurchaseDate is null || string.IsNullOrWhiteSpace(detail.PurchaseCurrency))
            return new(SpecializedAssetMutationResult.Invalid, Error: "Purchase price, purchase date and currency are required for the internal estimate.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var years = Math.Max(0m, (today.DayNumber - detail.PurchaseDate.Value.DayNumber) / 365.2425m);
        var retainedFactor = 1m - request.AnnualDepreciationPercent / 100m;
        var depreciated = detail.PurchasePrice.Value * (decimal)Math.Pow((double)retainedFactor, (double)years);
        var adjustmentPercent = request.MileageAdjustmentPercent + request.ConditionAdjustmentPercent;
        var amount = Math.Max(0m, Math.Round(depreciated * (1m + adjustmentPercent / 100m), 2));
        var range = request.RangePercent / 100m;
        var low = Math.Round(amount * (1m - range), 2);
        var high = Math.Round(amount * (1m + range), 2);

        audit.Record(fullWorthSpaceId, userId, "action.internal_vehicle_valuation.calculated", "Asset", assetId);
        await db.SaveChangesAsync(ct);

        return new(SpecializedAssetMutationResult.Success, new SpecializedAssetEstimateView(
            amount,
            low,
            high,
            detail.PurchaseCurrency,
            today,
            "internal_estimate",
            new Dictionary<string, object?>
            {
                ["purchasePrice"] = detail.PurchasePrice,
                ["purchaseDate"] = detail.PurchaseDate,
                ["annualDepreciationPercent"] = request.AnnualDepreciationPercent,
                ["mileageAdjustmentPercent"] = request.MileageAdjustmentPercent,
                ["conditionAdjustmentPercent"] = request.ConditionAdjustmentPercent,
                ["rangePercent"] = request.RangePercent
            },
            new[]
            {
                "Depreciation and adjustments are supplied by the user; FullWorth does not infer a vehicle market price.",
                "Mileage and condition adjustments are additive percentages applied after depreciation.",
                "The result is informational until explicitly accepted as an asset valuation."
            }));
    }

    public async Task<SpecializedAssetMutationResult> DeleteVehicleAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.Vehicle, ct);
        if (access != SpecializedAssetMutationResult.Success) return access;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"VehicleAssetDetails\" WHERE \"AssetId\" = {assetId};", ct);
        if (affected == 0) return SpecializedAssetMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "asset.vehicle.details_deleted", "Asset", assetId);
        await db.SaveChangesAsync(ct);
        return SpecializedAssetMutationResult.Success;
    }

    public async Task<SpecializedAssetOutcome<PreciousMetalDetailView?>> GetMetalAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadAsync(userId, fullWorthSpaceId, assetId, AssetKinds.PreciousMetal, ct))
            return new(SpecializedAssetMutationResult.NotFound);

        return new(SpecializedAssetMutationResult.Success, await ReadMetalAsync(assetId, ct));
    }

    public async Task<SpecializedAssetOutcome<PreciousMetalDetailView>> PutMetalAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, PreciousMetalDetailWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.PreciousMetal, ct);
        if (access != SpecializedAssetMutationResult.Success) return new(access);

        var error = ValidateMetal(request);
        if (error is not null) return new(SpecializedAssetMutationResult.Invalid, Error: error);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PreciousMetalAssetDetails"
                ("AssetId", "MetalType", "Form", "Quantity", "GrossWeightGrams", "Purity", "StorageLabel",
                 "PurchaseDate", "PurchasePrice", "PurchaseCurrency", "Notes", "UpdatedAt")
            VALUES
                ({assetId}, {Normalize(request.MetalType)}, {Normalize(request.Form)}, {request.Quantity},
                 {request.GrossWeightGrams}, {request.Purity}, {Trim(request.StorageLabel)}, {request.PurchaseDate},
                 {request.PurchasePrice}, {NormalizeCurrency(request.PurchaseCurrency)}, {Trim(request.Notes)}, now())
            ON CONFLICT ("AssetId") DO UPDATE SET
                "MetalType" = EXCLUDED."MetalType",
                "Form" = EXCLUDED."Form",
                "Quantity" = EXCLUDED."Quantity",
                "GrossWeightGrams" = EXCLUDED."GrossWeightGrams",
                "Purity" = EXCLUDED."Purity",
                "StorageLabel" = EXCLUDED."StorageLabel",
                "PurchaseDate" = EXCLUDED."PurchaseDate",
                "PurchasePrice" = EXCLUDED."PurchasePrice",
                "PurchaseCurrency" = EXCLUDED."PurchaseCurrency",
                "Notes" = EXCLUDED."Notes",
                "UpdatedAt" = now();
            """, ct);

        audit.Record(fullWorthSpaceId, userId, "asset.precious_metal.updated", "Asset", assetId);
        await db.SaveChangesAsync(ct);
        var view = await ReadMetalAsync(assetId, ct);
        return view is null
            ? new(SpecializedAssetMutationResult.NotFound)
            : new(SpecializedAssetMutationResult.Success, view);
    }

    public async Task<SpecializedAssetOutcome<SpecializedAssetEstimateView>> EstimateMetalAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, PreciousMetalEstimateWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.PreciousMetal, ct);
        if (access != SpecializedAssetMutationResult.Success) return new(access);

        if (request.ReferencePricePerFineGram <= 0m)
            return new(SpecializedAssetMutationResult.Invalid, Error: "Reference price per fine gram must be greater than zero.");
        var currency = NormalizeCurrency(request.Currency);
        if (!ValidCurrency(currency))
            return new(SpecializedAssetMutationResult.Invalid, Error: "Currency must be a three-letter code.");
        if (request.PremiumAdjustmentPercent is < -50m or > 100m)
            return new(SpecializedAssetMutationResult.Invalid, Error: "Premium adjustment must be between -50 and 100 percent.");
        if (request.RangePercent is < 0m or > 50m)
            return new(SpecializedAssetMutationResult.Invalid, Error: "Estimate range must be between 0 and 50 percent.");

        var detail = await ReadMetalAsync(assetId, ct);
        if (detail?.FineWeightGrams is not > 0m)
            return new(SpecializedAssetMutationResult.Invalid, Error: "Quantity, gross weight and purity are required for the internal estimate.");

        var amount = Math.Max(0m, Math.Round(
            detail.FineWeightGrams.Value * request.ReferencePricePerFineGram * (1m + request.PremiumAdjustmentPercent / 100m), 2));
        var range = request.RangePercent / 100m;
        var low = Math.Round(amount * (1m - range), 2);
        var high = Math.Round(amount * (1m + range), 2);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        audit.Record(fullWorthSpaceId, userId, "action.internal_precious_metal_valuation.calculated", "Asset", assetId);
        await db.SaveChangesAsync(ct);

        return new(SpecializedAssetMutationResult.Success, new SpecializedAssetEstimateView(
            amount,
            low,
            high,
            currency!,
            today,
            "internal_estimate",
            new Dictionary<string, object?>
            {
                ["metalType"] = detail.MetalType,
                ["quantity"] = detail.Quantity,
                ["grossWeightGramsPerUnit"] = detail.GrossWeightGrams,
                ["purity"] = detail.Purity,
                ["fineWeightGrams"] = detail.FineWeightGrams,
                ["referencePricePerFineGram"] = request.ReferencePricePerFineGram,
                ["premiumAdjustmentPercent"] = request.PremiumAdjustmentPercent,
                ["rangePercent"] = request.RangePercent
            },
            new[]
            {
                "The reference price per fine gram is supplied by the user; FullWorth does not duplicate securities market-data services.",
                "Fine weight equals quantity × gross weight per unit × purity.",
                "The result is informational until explicitly accepted as an asset valuation."
            }));
    }

    public async Task<SpecializedAssetMutationResult> DeleteMetalAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, AssetKinds.PreciousMetal, ct);
        if (access != SpecializedAssetMutationResult.Success) return access;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"PreciousMetalAssetDetails\" WHERE \"AssetId\" = {assetId};", ct);
        if (affected == 0) return SpecializedAssetMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "asset.precious_metal.details_deleted", "Asset", assetId);
        await db.SaveChangesAsync(ct);
        return SpecializedAssetMutationResult.Success;
    }

    private async Task<VehicleDetailView?> ReadVehicleAsync(Guid assetId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"VehicleAssetDetails\" WHERE \"AssetId\" = @id;";
            AddParameter(command, "@id", assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new VehicleDetailView(
                assetId,
                reader.GetString(reader.GetOrdinal("VehicleType")),
                ReadString(reader, "Manufacturer"),
                ReadString(reader, "Model"),
                ReadString(reader, "Variant"),
                ReadString(reader, "VIN"),
                ReadString(reader, "LicensePlate"),
                ReadDate(reader, "FirstRegistrationDate"),
                ReadInt(reader, "ModelYear"),
                ReadInt(reader, "MileageKm"),
                ReadString(reader, "Powertrain"),
                ReadDecimal(reader, "PowerKw"),
                ReadDate(reader, "PurchaseDate"),
                ReadDecimal(reader, "PurchasePrice"),
                ReadString(reader, "PurchaseCurrency"),
                ReadString(reader, "Condition"),
                ReadInt(reader, "AnnualMileageEstimate"),
                ReadString(reader, "Notes"),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("UpdatedAt")));
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    private async Task<PreciousMetalDetailView?> ReadMetalAsync(Guid assetId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"PreciousMetalAssetDetails\" WHERE \"AssetId\" = @id;";
            AddParameter(command, "@id", assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var quantity = reader.GetDecimal(reader.GetOrdinal("Quantity"));
            var grossWeight = ReadDecimal(reader, "GrossWeightGrams");
            var purity = ReadDecimal(reader, "Purity");
            decimal? fineWeight = grossWeight.HasValue && purity.HasValue
                ? Math.Round(quantity * grossWeight.Value * purity.Value, 8)
                : null;
            return new PreciousMetalDetailView(
                assetId,
                reader.GetString(reader.GetOrdinal("MetalType")),
                reader.GetString(reader.GetOrdinal("Form")),
                quantity,
                grossWeight,
                purity,
                fineWeight,
                ReadString(reader, "StorageLabel"),
                ReadDate(reader, "PurchaseDate"),
                ReadDecimal(reader, "PurchasePrice"),
                ReadString(reader, "PurchaseCurrency"),
                ReadString(reader, "Notes"),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("UpdatedAt")));
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    private static string? ValidateVehicle(VehicleDetailWrite request)
    {
        if (!VehicleTypes.Contains(Normalize(request.VehicleType))) return "Unsupported vehicle type.";
        if (request.Powertrain is not null && !Powertrains.Contains(Normalize(request.Powertrain))) return "Unsupported powertrain.";
        if (request.ModelYear is < 1886 or > 2200) return "Model year is outside the supported range.";
        if (request.MileageKm is < 0 || request.PowerKw is < 0 || request.PurchasePrice is < 0 || request.AnnualMileageEstimate is < 0)
            return "Vehicle numeric values cannot be negative.";
        if (request.PurchaseCurrency is not null && !ValidCurrency(NormalizeCurrency(request.PurchaseCurrency))) return "Invalid purchase currency.";
        if (TooLong(request.Manufacturer, 120) || TooLong(request.Model, 120) || TooLong(request.Variant, 120) || TooLong(request.Vin, 80) ||
            TooLong(request.LicensePlate, 40) || TooLong(request.Condition, 32) || TooLong(request.Notes, 2000)) return "One or more vehicle fields are too long.";
        return null;
    }

    private static string? ValidateMetal(PreciousMetalDetailWrite request)
    {
        if (!MetalTypes.Contains(Normalize(request.MetalType))) return "Unsupported precious-metal type.";
        if (!MetalForms.Contains(Normalize(request.Form))) return "Unsupported precious-metal form.";
        if (request.Quantity <= 0m) return "Quantity must be greater than zero.";
        if (request.GrossWeightGrams is < 0m || request.Purity is < 0m or > 1m || request.PurchasePrice is < 0m)
            return "Precious-metal numeric values are invalid.";
        if (request.PurchaseCurrency is not null && !ValidCurrency(NormalizeCurrency(request.PurchaseCurrency))) return "Invalid purchase currency.";
        if (TooLong(request.StorageLabel, 200) || TooLong(request.Notes, 2000)) return "One or more precious-metal fields are too long.";
        return null;
    }

    private Task<bool> CanReadAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, string kind, CancellationToken ct) =>
        db.Assets.AsNoTracking().AnyAsync(asset =>
            asset.Id == assetId && asset.FullWorthSpaceId == fullWorthSpaceId && asset.Kind == kind &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId), ct);

    private async Task<SpecializedAssetMutationResult> WriteAccessAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, string kind, CancellationToken ct)
    {
        if (!await CanReadAsync(userId, fullWorthSpaceId, assetId, kind, ct)) return SpecializedAssetMutationResult.NotFound;
        return await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
            member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner, ct)
            ? SpecializedAssetMutationResult.Success
            : SpecializedAssetMutationResult.Forbidden;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static string? NormalizeNullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : Normalize(value);
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeCurrency(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(char.IsAsciiLetterUpper);
    private static bool TooLong(string? value, int max) => value?.Trim().Length > max;

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? ReadString(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static decimal? ReadDecimal(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }

    private static int? ReadInt(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static DateOnly? ReadDate(DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateOnly>(ordinal);
    }
}
