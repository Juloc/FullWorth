using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class RealEstateStore(FullWorthDbContext db, AuditService audit, CurrencyConverter fx)
{
    public async Task<RealEstateMutationOutcome<RealEstatePropertyView>> GetPropertyAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var asset = await VisibleProperty(userId, fullWorthSpaceId, assetId).SingleOrDefaultAsync(ct);
        if (asset is null) return new(RealEstateMutationResult.NotFound);
        return new(RealEstateMutationResult.Success, new RealEstatePropertyView(
            asset.Id, asset.Name, asset.CurrentValue, asset.Currency, asset.ValuedAt, asset.IncludeInNetWorth,
            await ReadDetailAsync(assetId, ct)));
    }

    public async Task<RealEstateMutationOutcome<RealEstatePropertyView>> UpsertDetailAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, RealEstateDetailWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, requireRealEstate: true, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (RealEstateValidation.Validate(request) is { } error) return new(RealEstateMutationResult.Invalid, Error: error);
        RealEstateValidation.Normalize(request);

        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = UpsertDetailSql;
            AddDetailParameters(command, assetId, request);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }

        audit.Record(fullWorthSpaceId, userId, "property.updated", "RealEstateAssetDetail", assetId);
        await db.SaveChangesAsync(ct);
        return await GetPropertyAsync(userId, fullWorthSpaceId, assetId, ct);
    }

    public async Task<RealEstateMutationOutcome<IReadOnlyList<RealEstateAcquisitionCostView>>> ListCostsAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await VisibleProperty(userId, fullWorthSpaceId, assetId).AnyAsync(ct))
            return new(RealEstateMutationResult.NotFound);
        return new(RealEstateMutationResult.Success, await ReadCostsAsync(assetId, ct));
    }

    public async Task<RealEstateMutationOutcome<RealEstateAcquisitionCostView>> CreateCostAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, RealEstateAcquisitionCostWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, requireRealEstate: true, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (!await DetailExistsAsync(assetId, ct))
            return new(RealEstateMutationResult.Invalid, Error: "Complete property details before adding acquisition costs.");
        if (RealEstateValidation.Validate(request) is { } error)
            return new(RealEstateMutationResult.Invalid, Error: error);

        var id = Guid.NewGuid();
        var type = request.Type.Trim().ToLowerInvariant();
        var currency = request.Currency.Trim().ToUpperInvariant();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "RealEstateAcquisitionCosts" ("Id","AssetId","Type","Amount","Currency","Date","Notes","CreatedAt","UpdatedAt")
VALUES ({id},{assetId},{type},{request.Amount},{currency},{request.Date},{RealEstateValidation.Trim(request.Notes)},now(),now());
""", ct);
        audit.Record(fullWorthSpaceId, userId, "property.acquisition_cost.created", "RealEstateAcquisitionCost", id);
        await db.SaveChangesAsync(ct);
        var created = (await ReadCostsAsync(assetId, ct)).Single(item => item.Id == id);
        return new(RealEstateMutationResult.Success, created);
    }

    public async Task<RealEstateMutationResult> DeleteCostAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid costId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, requireRealEstate: true, ct);
        if (access != RealEstateMutationResult.Success) return access;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
DELETE FROM "RealEstateAcquisitionCosts" WHERE "Id"={costId} AND "AssetId"={assetId};
""", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "property.acquisition_cost.deleted", "RealEstateAcquisitionCost", costId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    public async Task<RealEstateMutationOutcome<IReadOnlyList<AssetDebtLinkView>>> ListDebtLinksAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await VisibleAsset(userId, fullWorthSpaceId, assetId).AnyAsync(ct))
            return new(RealEstateMutationResult.NotFound);
        return new(RealEstateMutationResult.Success, await ReadDebtLinksAsync(fullWorthSpaceId, assetId, ct));
    }

    public async Task<RealEstateMutationOutcome<AssetDebtLinkView>> CreateDebtLinkAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, AssetDebtLinkWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, requireRealEstate: false, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (await ValidateDebtLinkAsync(fullWorthSpaceId, request, null, ct) is { } error)
            return new(RealEstateMutationResult.Invalid, Error: error);

        var id = Guid.NewGuid();
        var relation = request.RelationType.Trim().ToLowerInvariant();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "AssetDebtLinks" ("Id","FullWorthSpaceId","AssetId","LoanId","LiabilityId","RelationType","AllocationPercent","CreatedAt")
VALUES ({id},{fullWorthSpaceId},{assetId},{request.LoanId},{request.LiabilityId},{relation},{request.AllocationPercent},now());
""", ct);
        audit.Record(fullWorthSpaceId, userId, "asset.debt_link.created", "AssetDebtLink", id);
        await db.SaveChangesAsync(ct);
        var created = (await ReadDebtLinksAsync(fullWorthSpaceId, assetId, ct)).Single(item => item.Id == id);
        return new(RealEstateMutationResult.Success, created);
    }

    public async Task<RealEstateMutationResult> DeleteDebtLinkAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid linkId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, requireRealEstate: false, ct);
        if (access != RealEstateMutationResult.Success) return access;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
DELETE FROM "AssetDebtLinks" WHERE "Id"={linkId} AND "AssetId"={assetId} AND "FullWorthSpaceId"={fullWorthSpaceId};
""", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "asset.debt_link.deleted", "AssetDebtLink", linkId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    public Task<RealEstateMutationOutcome<RealEstateMetricsView>> GetMetricsAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct) =>
        RealEstateMetrics.CalculateAsync(db, fx, userId, fullWorthSpaceId, assetId, ReadDetailAsync, ReadCostsAsync, ReadDebtLinksAsync, ct);

    internal async Task<RealEstateDetailView?> ReadDetailAsync(Guid assetId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"RealEstateAssetDetails\" WHERE \"AssetId\"=@asset;";
            AddParameter(command, "@asset", assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return ReadDetail(reader, assetId);
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }

    internal async Task<List<RealEstateAcquisitionCostView>> ReadCostsAsync(Guid assetId, CancellationToken ct)
    {
        var result = new List<RealEstateAcquisitionCostView>();
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Id\",\"AssetId\",\"Type\",\"Amount\",\"Currency\",\"Date\",\"Notes\",\"CreatedAt\",\"UpdatedAt\" FROM \"RealEstateAcquisitionCosts\" WHERE \"AssetId\"=@asset ORDER BY \"Date\",\"CreatedAt\",\"Id\";";
            AddParameter(command, "@asset", assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new RealEstateAcquisitionCostView(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetDecimal(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8)));
            }
            return result;
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }

    internal async Task<List<AssetDebtLinkView>> ReadDebtLinksAsync(Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var result = new List<AssetDebtLinkView>();
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = DebtLinksSql;
            AddParameter(command, "@space", fullWorthSpaceId);
            AddParameter(command, "@asset", assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new AssetDebtLinkView(
                    reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.GetString(4), reader.GetDecimal(5), reader.GetFieldValue<DateTimeOffset>(6), reader.GetString(7), reader.GetString(8),
                    reader.GetDecimal(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetDecimal(11), reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                    reader.IsDBNull(13) ? null : reader.GetDecimal(13), reader.IsDBNull(14) ? null : reader.GetFieldValue<DateOnly>(14),
                    reader.IsDBNull(15) ? null : reader.GetFieldValue<DateOnly>(15)));
            }
            return result;
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }

    private IQueryable<Asset> VisibleProperty(Guid userId, Guid fullWorthSpaceId, Guid assetId) =>
        VisibleAsset(userId, fullWorthSpaceId, assetId).Where(asset => asset.Kind == AssetKinds.RealEstate);

    private IQueryable<Asset> VisibleAsset(Guid userId, Guid fullWorthSpaceId, Guid assetId) =>
        db.Assets.AsNoTracking().Where(asset =>
            asset.Id == assetId && asset.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId));

    private async Task<RealEstateMutationResult> WriteAccessAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, bool requireRealEstate, CancellationToken ct)
    {
        var exists = requireRealEstate
            ? await VisibleProperty(userId, fullWorthSpaceId, assetId).AnyAsync(ct)
            : await VisibleAsset(userId, fullWorthSpaceId, assetId).AnyAsync(ct);
        if (!exists) return RealEstateMutationResult.NotFound;
        var owner = await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
            member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner, ct);
        return owner ? RealEstateMutationResult.Success : RealEstateMutationResult.Forbidden;
    }

    private async Task<string?> ValidateDebtLinkAsync(Guid fullWorthSpaceId, AssetDebtLinkWrite request, Guid? existingId, CancellationToken ct)
    {
        if (request.LoanId.HasValue == request.LiabilityId.HasValue)
            return "Exactly one of loanId or liabilityId is required.";
        var relation = request.RelationType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RealEstateValidation.RelationTypes.Contains(relation)) return "Unsupported debt relation type.";
        if (request.AllocationPercent <= 0m || request.AllocationPercent > 100m)
            return "Allocation percent must be greater than zero and at most 100.";

        decimal allocated;
        if (request.LoanId.HasValue)
        {
            if (!await db.Loans.AsNoTracking().AnyAsync(loan => loan.Id == request.LoanId.Value && loan.FullWorthSpaceId == fullWorthSpaceId, ct))
                return "Linked loan was not found in this FullWorth Space.";
            allocated = await SumDebtAllocationAsync("LoanId", request.LoanId.Value, existingId, ct);
        }
        else
        {
            if (!await db.Liabilities.AsNoTracking().AnyAsync(item => item.Id == request.LiabilityId!.Value && item.FullWorthSpaceId == fullWorthSpaceId, ct))
                return "Linked liability was not found in this FullWorth Space.";
            allocated = await SumDebtAllocationAsync("LiabilityId", request.LiabilityId!.Value, existingId, ct);
        }
        return allocated + request.AllocationPercent > 100m ? "Debt allocation across assets cannot exceed 100 percent." : null;
    }

    private async Task<decimal> SumDebtAllocationAsync(string column, Guid debtId, Guid? existingId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            // @existing is a nullable uuid; cast it so Postgres can resolve the parameter type in the
            // "@existing IS NULL" branch (an untyped NULL there raises 42P08 could-not-determine-type).
            command.CommandText = $"SELECT COALESCE(SUM(\"AllocationPercent\"),0) FROM \"AssetDebtLinks\" WHERE \"{column}\"=@debt AND (@existing::uuid IS NULL OR \"Id\"<>@existing::uuid);";
            AddParameter(command, "@debt", debtId);
            AddParameter(command, "@existing", existingId);
            return Convert.ToDecimal(await command.ExecuteScalarAsync(ct));
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }

    private async Task<bool> DetailExistsAsync(Guid assetId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM \"RealEstateAssetDetails\" WHERE \"AssetId\"=@asset;";
            AddParameter(command, "@asset", assetId);
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }

    private static RealEstateDetailView ReadDetail(DbDataReader reader, Guid assetId) => new()
    {
        AssetId = assetId,
        PropertyType = GetString(reader, "PropertyType")!, UsageType = GetString(reader, "UsageType")!, CountryCode = GetString(reader, "CountryCode")!,
        PostalCode = GetString(reader, "PostalCode"), City = GetString(reader, "City"), Street = GetString(reader, "Street"), HouseNumber = GetString(reader, "HouseNumber"),
        AddressExtra = GetString(reader, "AddressExtra"), UnitLabel = GetString(reader, "UnitLabel"), Latitude = GetDecimal(reader, "Latitude"), Longitude = GetDecimal(reader, "Longitude"),
        YearBuilt = GetInt(reader, "YearBuilt"), LastMajorModernizationYear = GetInt(reader, "LastMajorModernizationYear"), LivingAreaSqm = GetDecimal(reader, "LivingAreaSqm"),
        UsableAreaSqm = GetDecimal(reader, "UsableAreaSqm"), PlotAreaSqm = GetDecimal(reader, "PlotAreaSqm"), Rooms = GetDecimal(reader, "Rooms"),
        Bedrooms = GetInt(reader, "Bedrooms"), Bathrooms = GetInt(reader, "Bathrooms"), Floor = GetInt(reader, "Floor"), TotalFloors = GetInt(reader, "TotalFloors"),
        OwnershipSharePercent = GetRequiredDecimal(reader, "OwnershipSharePercent"), ParkingSpaces = GetInt(reader, "ParkingSpaces"), GarageSpaces = GetInt(reader, "GarageSpaces"),
        Condition = GetString(reader, "Condition"), ConstructionType = GetString(reader, "ConstructionType"), HeatingType = GetString(reader, "HeatingType"),
        PrimaryEnergySource = GetString(reader, "PrimaryEnergySource"), Elevator = GetBool(reader, "Elevator"), BarrierFree = GetBool(reader, "BarrierFree"),
        BalconyTerrace = GetBool(reader, "BalconyTerrace"), Basement = GetBool(reader, "Basement"), Garden = GetBool(reader, "Garden"), PurchaseDate = GetDate(reader, "PurchaseDate"),
        PurchasePrice = GetDecimal(reader, "PurchasePrice"), PurchaseCurrency = GetString(reader, "PurchaseCurrency"), AcquisitionCosts = GetDecimal(reader, "AcquisitionCosts"),
        EquityAtPurchase = GetDecimal(reader, "EquityAtPurchase"), Notes = GetString(reader, "Notes"), UpdatedAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("UpdatedAt"))
    };

    private static void AddDetailParameters(DbCommand command, Guid assetId, RealEstateDetailWrite r)
    {
        AddParameter(command, "@asset", assetId); AddParameter(command, "@propertyType", r.PropertyType); AddParameter(command, "@usageType", r.UsageType);
        AddParameter(command, "@country", r.CountryCode); AddParameter(command, "@postal", r.PostalCode); AddParameter(command, "@city", r.City); AddParameter(command, "@street", r.Street);
        AddParameter(command, "@house", r.HouseNumber); AddParameter(command, "@extra", r.AddressExtra); AddParameter(command, "@unit", r.UnitLabel); AddParameter(command, "@lat", r.Latitude);
        AddParameter(command, "@lon", r.Longitude); AddParameter(command, "@built", r.YearBuilt); AddParameter(command, "@modernized", r.LastMajorModernizationYear);
        AddParameter(command, "@living", r.LivingAreaSqm); AddParameter(command, "@usable", r.UsableAreaSqm); AddParameter(command, "@plot", r.PlotAreaSqm); AddParameter(command, "@rooms", r.Rooms);
        AddParameter(command, "@bedrooms", r.Bedrooms); AddParameter(command, "@bathrooms", r.Bathrooms); AddParameter(command, "@floor", r.Floor); AddParameter(command, "@floors", r.TotalFloors);
        AddParameter(command, "@ownership", r.OwnershipSharePercent); AddParameter(command, "@parking", r.ParkingSpaces); AddParameter(command, "@garage", r.GarageSpaces);
        AddParameter(command, "@condition", r.Condition); AddParameter(command, "@construction", r.ConstructionType); AddParameter(command, "@heating", r.HeatingType);
        AddParameter(command, "@energy", r.PrimaryEnergySource); AddParameter(command, "@elevator", r.Elevator); AddParameter(command, "@barrierFree", r.BarrierFree);
        AddParameter(command, "@balcony", r.BalconyTerrace); AddParameter(command, "@basement", r.Basement); AddParameter(command, "@garden", r.Garden);
        AddParameter(command, "@purchaseDate", r.PurchaseDate); AddParameter(command, "@purchasePrice", r.PurchasePrice); AddParameter(command, "@purchaseCurrency", r.PurchaseCurrency);
        AddParameter(command, "@acquisition", r.AcquisitionCosts); AddParameter(command, "@equity", r.EquityAtPurchase); AddParameter(command, "@notes", r.Notes);
    }

    private const string UpsertDetailSql = """
INSERT INTO "RealEstateAssetDetails" (
 "AssetId","PropertyType","UsageType","CountryCode","PostalCode","City","Street","HouseNumber","AddressExtra","UnitLabel","Latitude","Longitude",
 "YearBuilt","LastMajorModernizationYear","LivingAreaSqm","UsableAreaSqm","PlotAreaSqm","Rooms","Bedrooms","Bathrooms","Floor","TotalFloors",
 "OwnershipSharePercent","ParkingSpaces","GarageSpaces","Condition","ConstructionType","HeatingType","PrimaryEnergySource","Elevator","BarrierFree",
 "BalconyTerrace","Basement","Garden","PurchaseDate","PurchasePrice","PurchaseCurrency","AcquisitionCosts","EquityAtPurchase","Notes","UpdatedAt")
VALUES (@asset,@propertyType,@usageType,@country,@postal,@city,@street,@house,@extra,@unit,@lat,@lon,@built,@modernized,@living,@usable,@plot,@rooms,@bedrooms,@bathrooms,
 @floor,@floors,@ownership,@parking,@garage,@condition,@construction,@heating,@energy,@elevator,@barrierFree,@balcony,@basement,@garden,@purchaseDate,@purchasePrice,
 @purchaseCurrency,@acquisition,@equity,@notes,now())
ON CONFLICT ("AssetId") DO UPDATE SET
 "PropertyType"=EXCLUDED."PropertyType","UsageType"=EXCLUDED."UsageType","CountryCode"=EXCLUDED."CountryCode","PostalCode"=EXCLUDED."PostalCode",
 "City"=EXCLUDED."City","Street"=EXCLUDED."Street","HouseNumber"=EXCLUDED."HouseNumber","AddressExtra"=EXCLUDED."AddressExtra","UnitLabel"=EXCLUDED."UnitLabel",
 "Latitude"=EXCLUDED."Latitude","Longitude"=EXCLUDED."Longitude","YearBuilt"=EXCLUDED."YearBuilt","LastMajorModernizationYear"=EXCLUDED."LastMajorModernizationYear",
 "LivingAreaSqm"=EXCLUDED."LivingAreaSqm","UsableAreaSqm"=EXCLUDED."UsableAreaSqm","PlotAreaSqm"=EXCLUDED."PlotAreaSqm","Rooms"=EXCLUDED."Rooms",
 "Bedrooms"=EXCLUDED."Bedrooms","Bathrooms"=EXCLUDED."Bathrooms","Floor"=EXCLUDED."Floor","TotalFloors"=EXCLUDED."TotalFloors",
 "OwnershipSharePercent"=EXCLUDED."OwnershipSharePercent","ParkingSpaces"=EXCLUDED."ParkingSpaces","GarageSpaces"=EXCLUDED."GarageSpaces","Condition"=EXCLUDED."Condition",
 "ConstructionType"=EXCLUDED."ConstructionType","HeatingType"=EXCLUDED."HeatingType","PrimaryEnergySource"=EXCLUDED."PrimaryEnergySource","Elevator"=EXCLUDED."Elevator",
 "BarrierFree"=EXCLUDED."BarrierFree","BalconyTerrace"=EXCLUDED."BalconyTerrace","Basement"=EXCLUDED."Basement","Garden"=EXCLUDED."Garden",
 "PurchaseDate"=EXCLUDED."PurchaseDate","PurchasePrice"=EXCLUDED."PurchasePrice","PurchaseCurrency"=EXCLUDED."PurchaseCurrency","AcquisitionCosts"=EXCLUDED."AcquisitionCosts",
 "EquityAtPurchase"=EXCLUDED."EquityAtPurchase","Notes"=EXCLUDED."Notes","UpdatedAt"=now();
""";

    private const string DebtLinksSql = """
SELECT l."Id",l."AssetId",l."LoanId",l."LiabilityId",l."RelationType",l."AllocationPercent",l."CreatedAt",
       CASE WHEN l."LoanId" IS NOT NULL THEN 'loan' ELSE 'liability' END AS "DebtType",
       COALESCE(ln."Name",li."Name") AS "Name",COALESCE(ln."CurrentBalance",li."CurrentBalance") AS "CurrentBalance",
       COALESCE(ln."Currency",li."Currency") AS "Currency",ln."OriginalPrincipal",
       COALESCE(ln."NominalInterestRate",li."InterestRate") AS "InterestRate",COALESCE(ln."PaymentAmount",li."RegularPayment") AS "RegularPayment",
       ln."StartDate",COALESCE(ln."EndDate",li."EndDate") AS "EndDate"
FROM "AssetDebtLinks" l
LEFT JOIN "Loans" ln ON ln."Id"=l."LoanId"
LEFT JOIN "Liabilities" li ON li."Id"=l."LiabilityId"
WHERE l."FullWorthSpaceId"=@space AND l."AssetId"=@asset
ORDER BY "DebtType","Name",l."Id";
""";

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter);
    }
    private static string? GetString(DbDataReader r, string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetString(i); }
    private static decimal? GetDecimal(DbDataReader r, string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetDecimal(i); }
    private static decimal GetRequiredDecimal(DbDataReader r, string n) => r.GetDecimal(r.GetOrdinal(n));
    private static int? GetInt(DbDataReader r, string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetInt32(i); }
    private static bool? GetBool(DbDataReader r, string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetBoolean(i); }
    private static DateOnly? GetDate(DbDataReader r, string n) { var i = r.GetOrdinal(n); return r.IsDBNull(i) ? null : r.GetFieldValue<DateOnly>(i); }
}
