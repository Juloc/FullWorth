using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class PropertyRentalStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<RealEstateMutationOutcome<IReadOnlyList<PropertyUnitView>>> ListUnitsAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadPropertyAsync(userId, fullWorthSpaceId, assetId, ct)) return new(RealEstateMutationResult.NotFound);
        return new(RealEstateMutationResult.Success, await ReadUnitsAsync(fullWorthSpaceId, assetId, ct));
    }

    public async Task<RealEstateMutationOutcome<PropertyUnitView>> CreateUnitAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, PropertyUnitWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (ValidateUnit(request) is { } error) return new(RealEstateMutationResult.Invalid, Error: error);

        var id = Guid.NewGuid();
        var name = request.Name.Trim();
        var unitType = request.UnitType.Trim().ToLowerInvariant();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "PropertyUnits" ("Id","FullWorthSpaceId","AssetId","Name","UnitType","AreaSqm","Rooms","OwnershipSharePercent","IsOwnerOccupied","IsActive","Notes","CreatedAt","UpdatedAt")
VALUES ({id},{fullWorthSpaceId},{assetId},{name},{unitType},{request.AreaSqm},{request.Rooms},{request.OwnershipSharePercent},{request.IsOwnerOccupied},{request.IsActive},{Trim(request.Notes)},now(),now());
""", ct);
        audit.Record(fullWorthSpaceId, userId, "property.unit.created", "PropertyUnit", id);
        await db.SaveChangesAsync(ct);
        var created = (await ReadUnitsAsync(fullWorthSpaceId, assetId, ct)).Single(x => x.Id == id);
        return new(RealEstateMutationResult.Success, created);
    }

    public async Task<RealEstateMutationOutcome<PropertyUnitView>> UpdateUnitAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid unitId, PropertyUnitWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (ValidateUnit(request) is { } error) return new(RealEstateMutationResult.Invalid, Error: error);

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "PropertyUnits"
SET "Name"={request.Name.Trim()}, "UnitType"={request.UnitType.Trim().ToLowerInvariant()}, "AreaSqm"={request.AreaSqm},
    "Rooms"={request.Rooms}, "OwnershipSharePercent"={request.OwnershipSharePercent}, "IsOwnerOccupied"={request.IsOwnerOccupied},
    "IsActive"={request.IsActive}, "Notes"={Trim(request.Notes)}, "UpdatedAt"=now()
WHERE "Id"={unitId} AND "AssetId"={assetId} AND "FullWorthSpaceId"={fullWorthSpaceId};
""", ct);
        if (affected == 0) return new(RealEstateMutationResult.NotFound);
        audit.Record(fullWorthSpaceId, userId, "property.unit.updated", "PropertyUnit", unitId);
        await db.SaveChangesAsync(ct);
        var updated = (await ReadUnitsAsync(fullWorthSpaceId, assetId, ct)).Single(x => x.Id == unitId);
        return new(RealEstateMutationResult.Success, updated);
    }

    public async Task<RealEstateMutationResult> DeactivateUnitAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid unitId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return access;
        if (await HasActiveLeaseAsync(unitId, ct)) return RealEstateMutationResult.Invalid;

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "PropertyUnits" SET "IsActive"=false, "UpdatedAt"=now()
WHERE "Id"={unitId} AND "AssetId"={assetId} AND "FullWorthSpaceId"={fullWorthSpaceId};
""", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "property.unit.deactivated", "PropertyUnit", unitId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    public async Task<RealEstateMutationOutcome<IReadOnlyList<RentalLeaseView>>> ListLeasesAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadPropertyAsync(userId, fullWorthSpaceId, assetId, ct)) return new(RealEstateMutationResult.NotFound);
        return new(RealEstateMutationResult.Success, await ReadLeasesAsync(fullWorthSpaceId, assetId, ct));
    }

    public async Task<RealEstateMutationOutcome<RentalLeaseView>> CreateLeaseAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, RentalLeaseWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (ValidateLease(request) is { } error) return new(RealEstateMutationResult.Invalid, Error: error);
        if (!await UnitBelongsToPropertyAsync(fullWorthSpaceId, assetId, request.PropertyUnitId, ct))
            return new(RealEstateMutationResult.Invalid, Error: "Unit must belong to this property.");
        if (await LeaseOverlapsAsync(request.PropertyUnitId, null, request.Status, request.StartDate, request.EndDate, ct))
            return new(RealEstateMutationResult.Invalid, Error: "Active leases for one unit cannot overlap.");

        var id = Guid.NewGuid();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "RentalLeases" ("Id","FullWorthSpaceId","AssetId","PropertyUnitId","TenantDisplayLabel","StartDate","EndDate","Status","ColdRent","UtilitiesAdvance","OtherRecurringCharges","Currency","PaymentCycle","DepositAmount","DepositHeld","LastRentChangeDate","NextReviewDate","Notes","CreatedAt","UpdatedAt")
VALUES ({id},{fullWorthSpaceId},{assetId},{request.PropertyUnitId},{Trim(request.TenantDisplayLabel)},{request.StartDate},{request.EndDate},{request.Status.Trim().ToLowerInvariant()},
        {request.ColdRent},{request.UtilitiesAdvance},{request.OtherRecurringCharges},{request.Currency.Trim().ToUpperInvariant()},{request.PaymentCycle.Trim().ToLowerInvariant()},
        {request.DepositAmount},{request.DepositHeld},{request.LastRentChangeDate},{request.NextReviewDate},{Trim(request.Notes)},now(),now());
""", ct);
        }
        catch (PostgresException) { return new(RealEstateMutationResult.Invalid, Error: "Lease violates property constraints."); }

        audit.Record(fullWorthSpaceId, userId, "property.lease.created", "RentalLease", id);
        await db.SaveChangesAsync(ct);
        var created = (await ReadLeasesAsync(fullWorthSpaceId, assetId, ct)).Single(x => x.Id == id);
        return new(RealEstateMutationResult.Success, created);
    }

    public async Task<RealEstateMutationOutcome<RentalLeaseView>> UpdateLeaseAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid leaseId, RentalLeaseWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (ValidateLease(request) is { } error) return new(RealEstateMutationResult.Invalid, Error: error);
        if (!await UnitBelongsToPropertyAsync(fullWorthSpaceId, assetId, request.PropertyUnitId, ct))
            return new(RealEstateMutationResult.Invalid, Error: "Unit must belong to this property.");
        if (await LeaseOverlapsAsync(request.PropertyUnitId, leaseId, request.Status, request.StartDate, request.EndDate, ct))
            return new(RealEstateMutationResult.Invalid, Error: "Active leases for one unit cannot overlap.");

        int affected;
        try
        {
            affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "RentalLeases"
SET "PropertyUnitId"={request.PropertyUnitId}, "TenantDisplayLabel"={Trim(request.TenantDisplayLabel)}, "StartDate"={request.StartDate}, "EndDate"={request.EndDate},
    "Status"={request.Status.Trim().ToLowerInvariant()}, "ColdRent"={request.ColdRent}, "UtilitiesAdvance"={request.UtilitiesAdvance},
    "OtherRecurringCharges"={request.OtherRecurringCharges}, "Currency"={request.Currency.Trim().ToUpperInvariant()}, "PaymentCycle"={request.PaymentCycle.Trim().ToLowerInvariant()},
    "DepositAmount"={request.DepositAmount}, "DepositHeld"={request.DepositHeld}, "LastRentChangeDate"={request.LastRentChangeDate},
    "NextReviewDate"={request.NextReviewDate}, "Notes"={Trim(request.Notes)}, "UpdatedAt"=now()
WHERE "Id"={leaseId} AND "AssetId"={assetId} AND "FullWorthSpaceId"={fullWorthSpaceId};
""", ct);
        }
        catch (PostgresException) { return new(RealEstateMutationResult.Invalid, Error: "Lease violates property constraints."); }
        if (affected == 0) return new(RealEstateMutationResult.NotFound);

        audit.Record(fullWorthSpaceId, userId, "property.lease.updated", "RentalLease", leaseId);
        await db.SaveChangesAsync(ct);
        var updated = (await ReadLeasesAsync(fullWorthSpaceId, assetId, ct)).Single(x => x.Id == leaseId);
        return new(RealEstateMutationResult.Success, updated);
    }

    public async Task<RealEstateMutationResult> EndLeaseAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid leaseId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return access;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "RentalLeases"
SET "Status"='ended', "EndDate"=CASE WHEN "EndDate" IS NULL OR "EndDate">{today} THEN {today} ELSE "EndDate" END, "UpdatedAt"=now()
WHERE "Id"={leaseId} AND "AssetId"={assetId} AND "FullWorthSpaceId"={fullWorthSpaceId};
""", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "property.lease.ended", "RentalLease", leaseId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    internal async Task<List<PropertyUnitView>> ReadUnitsAsync(Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var result = new List<PropertyUnitView>();
        await WithReaderAsync("""
SELECT "Id","FullWorthSpaceId","AssetId","Name","UnitType","AreaSqm","Rooms","OwnershipSharePercent","IsOwnerOccupied","IsActive","Notes","CreatedAt","UpdatedAt"
FROM "PropertyUnits" WHERE "FullWorthSpaceId"=@space AND "AssetId"=@asset ORDER BY "IsActive" DESC,"Name","Id";
""", fullWorthSpaceId, assetId, ct, reader =>
        {
            result.Add(new PropertyUnitView(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                DecimalOrNull(reader, 5), DecimalOrNull(reader, 6), DecimalOrNull(reader, 7), reader.GetBoolean(8), reader.GetBoolean(9),
                StringOrNull(reader, 10), reader.GetFieldValue<DateTimeOffset>(11), reader.GetFieldValue<DateTimeOffset>(12)));
        });
        return result;
    }

    internal async Task<List<RentalLeaseView>> ReadLeasesAsync(Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var result = new List<RentalLeaseView>();
        await WithReaderAsync("""
SELECT l."Id",l."FullWorthSpaceId",l."AssetId",l."PropertyUnitId",u."Name",l."TenantDisplayLabel",l."StartDate",l."EndDate",l."Status",
       l."ColdRent",l."UtilitiesAdvance",l."OtherRecurringCharges",l."Currency",l."PaymentCycle",l."DepositAmount",l."DepositHeld",
       l."LastRentChangeDate",l."NextReviewDate",l."Notes",l."CreatedAt",l."UpdatedAt"
FROM "RentalLeases" l JOIN "PropertyUnits" u ON u."Id"=l."PropertyUnitId"
WHERE l."FullWorthSpaceId"=@space AND l."AssetId"=@asset
ORDER BY CASE l."Status" WHEN 'active' THEN 0 WHEN 'planned' THEN 1 ELSE 2 END,l."StartDate" DESC,l."Id";
""", fullWorthSpaceId, assetId, ct, reader =>
        {
            var cold = reader.GetDecimal(9);
            var utilities = DecimalOrNull(reader, 10);
            var other = DecimalOrNull(reader, 11);
            result.Add(new RentalLeaseView(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetString(4),
                StringOrNull(reader, 5), reader.GetFieldValue<DateOnly>(6), DateOrNull(reader, 7), reader.GetString(8), cold, utilities, other,
                cold + (utilities ?? 0m) + (other ?? 0m), reader.GetString(12), reader.GetString(13), DecimalOrNull(reader, 14),
                reader.IsDBNull(15) ? null : reader.GetBoolean(15), DateOrNull(reader, 16), DateOrNull(reader, 17), StringOrNull(reader, 18),
                reader.GetFieldValue<DateTimeOffset>(19), reader.GetFieldValue<DateTimeOffset>(20)));
        });
        return result;
    }

    private async Task<bool> CanReadPropertyAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct) =>
        await db.Assets.AsNoTracking().AnyAsync(asset => asset.Id == assetId && asset.FullWorthSpaceId == fullWorthSpaceId && asset.Kind == AssetKinds.RealEstate &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId), ct);

    private async Task<RealEstateMutationResult> WriteAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadPropertyAsync(userId, fullWorthSpaceId, assetId, ct)) return RealEstateMutationResult.NotFound;
        return await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner, ct)
            ? RealEstateMutationResult.Success : RealEstateMutationResult.Forbidden;
    }

    private async Task<bool> UnitBelongsToPropertyAsync(Guid fullWorthSpaceId, Guid assetId, Guid unitId, CancellationToken ct)
    {
        return await ScalarExistsAsync("SELECT 1 FROM \"PropertyUnits\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"AssetId\"=@asset AND \"IsActive\"=true;",
            ct, ("@id", unitId), ("@space", fullWorthSpaceId), ("@asset", assetId));
    }

    private async Task<bool> HasActiveLeaseAsync(Guid unitId, CancellationToken ct) =>
        await ScalarExistsAsync("SELECT 1 FROM \"RentalLeases\" WHERE \"PropertyUnitId\"=@id AND \"Status\"='active';", ct, ("@id", unitId));

    private async Task<bool> LeaseOverlapsAsync(Guid unitId, Guid? existingId, string status, DateOnly start, DateOnly? end, CancellationToken ct)
    {
        if (!string.Equals(status.Trim(), "active", StringComparison.OrdinalIgnoreCase)) return false;
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT 1 FROM "RentalLeases"
WHERE "PropertyUnitId"=@unit AND "Status"='active' AND (@id::uuid IS NULL OR "Id"<>@id)
  AND daterange("StartDate",COALESCE("EndDate",'infinity'::date),'[]') && daterange(@start,COALESCE(@end,'infinity'::date),'[]')
LIMIT 1;
""";
            AddParameter(command, "@unit", unitId); AddParameter(command, "@id", existingId); AddParameter(command, "@start", start); AddParameter(command, "@end", end);
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private static string? ValidateUnit(PropertyUnitWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 160) return "Unit name is required and limited to 160 characters.";
        var type = request.UnitType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RealEstateOperationsKinds.UnitTypes.Contains(type)) return "Unsupported unit type.";
        if (request.AreaSqm is < 0m || request.Rooms is < 0m) return "Unit area and rooms cannot be negative.";
        if (request.OwnershipSharePercent is <= 0m or > 100m) return "Unit ownership share must be greater than zero and at most 100 percent.";
        return null;
    }

    private static string? ValidateLease(RentalLeaseWrite request)
    {
        var status = request.Status?.Trim().ToLowerInvariant() ?? string.Empty;
        var cycle = request.PaymentCycle?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RealEstateOperationsKinds.LeaseStatuses.Contains(status)) return "Unsupported lease status.";
        if (!RealEstateOperationsKinds.PaymentCycles.Contains(cycle)) return "Unsupported payment cycle.";
        if (request.EndDate.HasValue && request.EndDate.Value < request.StartDate) return "Lease end date cannot be before start date.";
        if (request.ColdRent < 0m || request.UtilitiesAdvance is < 0m || request.OtherRecurringCharges is < 0m || request.DepositAmount is < 0m) return "Lease amounts cannot be negative.";
        if (!RealEstateValidation.ValidCurrency(request.Currency)) return "Currency must contain three letters.";
        return null;
    }

    private async Task WithReaderAsync(string sql, Guid space, Guid asset, CancellationToken ct, Action<DbDataReader> read)
    {
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open; if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand(); command.CommandText = sql; AddParameter(command, "@space", space); AddParameter(command, "@asset", asset);
            await using var reader = await command.ExecuteReaderAsync(ct); while (await reader.ReadAsync(ct)) read(reader);
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private async Task<bool> ScalarExistsAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open; if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var parameter in parameters) AddParameter(command, parameter.Name, parameter.Value);
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private static decimal? DecimalOrNull(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    private static DateOnly? DateOrNull(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateOnly>(ordinal);
    private static string? StringOrNull(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void AddParameter(DbCommand command, string name, object? value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
}
