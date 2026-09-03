using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class RealEstateUpdateStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<RealEstateMutationResult> UpdateCostAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid assetId,
        Guid costId,
        RealEstateAcquisitionCostWrite request,
        CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, requireRealEstate: true, ct);
        if (access != RealEstateMutationResult.Success) return access;
        if (RealEstateValidation.Validate(request) is not null) return RealEstateMutationResult.Invalid;

        var type = request.Type.Trim().ToLowerInvariant();
        var currency = request.Currency.Trim().ToUpperInvariant();
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "RealEstateAcquisitionCosts"
SET "Type"={type}, "Amount"={request.Amount}, "Currency"={currency}, "Date"={request.Date},
    "Notes"={RealEstateValidation.Trim(request.Notes)}, "UpdatedAt"=now()
WHERE "Id"={costId} AND "AssetId"={assetId};
""", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;

        audit.Record(fullWorthSpaceId, userId, "property.acquisition_cost.updated", "RealEstateAcquisitionCost", costId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    public async Task<RealEstateMutationResult> UpdateDebtLinkAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid assetId,
        Guid linkId,
        AssetDebtLinkWrite request,
        CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, requireRealEstate: false, ct);
        if (access != RealEstateMutationResult.Success) return access;
        if (request.LoanId.HasValue == request.LiabilityId.HasValue) return RealEstateMutationResult.Invalid;

        var relation = request.RelationType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RealEstateValidation.RelationTypes.Contains(relation)) return RealEstateMutationResult.Invalid;
        if (request.AllocationPercent <= 0m || request.AllocationPercent > 100m) return RealEstateMutationResult.Invalid;
        if (!await LinkExistsAsync(fullWorthSpaceId, assetId, linkId, ct)) return RealEstateMutationResult.NotFound;

        Guid debtId;
        string debtColumn;
        if (request.LoanId is { } loanId)
        {
            if (!await db.Loans.AsNoTracking().AnyAsync(loan => loan.Id == loanId && loan.FullWorthSpaceId == fullWorthSpaceId, ct))
                return RealEstateMutationResult.Invalid;
            debtId = loanId;
            debtColumn = "LoanId";
        }
        else
        {
            var liabilityId = request.LiabilityId!.Value;
            if (!await db.Liabilities.AsNoTracking().AnyAsync(item => item.Id == liabilityId && item.FullWorthSpaceId == fullWorthSpaceId, ct))
                return RealEstateMutationResult.Invalid;
            debtId = liabilityId;
            debtColumn = "LiabilityId";
        }

        if (await DuplicateDebtOnAssetAsync(assetId, linkId, debtColumn, debtId, ct))
            return RealEstateMutationResult.Invalid;

        var allocated = await SumAllocationAsync(debtColumn, debtId, linkId, ct);
        if (allocated + request.AllocationPercent > 100m) return RealEstateMutationResult.Invalid;

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "AssetDebtLinks"
SET "LoanId"={request.LoanId}, "LiabilityId"={request.LiabilityId}, "RelationType"={relation},
    "AllocationPercent"={request.AllocationPercent}
WHERE "Id"={linkId} AND "AssetId"={assetId} AND "FullWorthSpaceId"={fullWorthSpaceId};
""", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;

        audit.Record(fullWorthSpaceId, userId, "asset.debt_link.updated", "AssetDebtLink", linkId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    private async Task<RealEstateMutationResult> WriteAccessAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid assetId,
        bool requireRealEstate,
        CancellationToken ct)
    {
        var exists = await db.Assets.AsNoTracking().AnyAsync(asset =>
            asset.Id == assetId &&
            asset.FullWorthSpaceId == fullWorthSpaceId &&
            (!requireRealEstate || asset.Kind == AssetKinds.RealEstate) &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId), ct);
        if (!exists) return RealEstateMutationResult.NotFound;

        var owner = await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
            member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner, ct);
        return owner ? RealEstateMutationResult.Success : RealEstateMutationResult.Forbidden;
    }

    private Task<bool> LinkExistsAsync(Guid fullWorthSpaceId, Guid assetId, Guid linkId, CancellationToken ct) =>
        ScalarExistsAsync(
            "SELECT 1 FROM \"AssetDebtLinks\" WHERE \"Id\"=@id AND \"AssetId\"=@asset AND \"FullWorthSpaceId\"=@space;",
            ct,
            ("@id", linkId), ("@asset", assetId), ("@space", fullWorthSpaceId));

    private Task<bool> DuplicateDebtOnAssetAsync(
        Guid assetId,
        Guid linkId,
        string debtColumn,
        Guid debtId,
        CancellationToken ct)
    {
        var sql = $"SELECT 1 FROM \"AssetDebtLinks\" WHERE \"AssetId\"=@asset AND \"Id\"<>@id AND \"{debtColumn}\"=@debt;";
        return ScalarExistsAsync(sql, ct, ("@asset", assetId), ("@id", linkId), ("@debt", debtId));
    }

    private async Task<decimal> SumAllocationAsync(string column, Guid debtId, Guid linkId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COALESCE(SUM(\"AllocationPercent\"),0) FROM \"AssetDebtLinks\" WHERE \"{column}\"=@debt AND \"Id\"<>@id;";
            AddParameter(command, "@debt", debtId);
            AddParameter(command, "@id", linkId);
            return Convert.ToDecimal(await command.ExecuteScalarAsync(ct));
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }

    private async Task<bool> ScalarExistsAsync(
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) AddParameter(command, name, value);
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally
        {
            if (close) await connection.CloseAsync();
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
