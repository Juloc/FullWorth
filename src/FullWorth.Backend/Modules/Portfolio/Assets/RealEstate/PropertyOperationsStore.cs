using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class PropertyOperationsStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<RealEstateMutationOutcome<IReadOnlyList<PropertyImprovementView>>> ListImprovementsAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadPropertyAsync(userId, fullWorthSpaceId, assetId, ct)) return new(RealEstateMutationResult.NotFound);
        return new(RealEstateMutationResult.Success, await ReadImprovementsAsync(assetId, ct));
    }

    public async Task<RealEstateMutationOutcome<PropertyImprovementView>> CreateImprovementAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, PropertyImprovementWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (ValidateImprovement(request) is { } error) return new(RealEstateMutationResult.Invalid, Error: error);
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "PropertyImprovements" ("Id","AssetId","Title","Category","StartDate","CompletedDate","Cost","Currency","EstimatedValueAdded","Description","DocumentId","CreatedAt","UpdatedAt")
VALUES ({id},{assetId},{request.Title.Trim()},{request.Category.Trim().ToLowerInvariant()},{request.StartDate},{request.CompletedDate},{request.Cost},
        {NormalizeCurrency(request.Currency)},{request.EstimatedValueAdded},{Trim(request.Description)},NULL,now(),now());
""", ct);
        audit.Record(fullWorthSpaceId, userId, "property.improvement.created", "PropertyImprovement", id);
        await db.SaveChangesAsync(ct);
        return new(RealEstateMutationResult.Success, (await ReadImprovementsAsync(assetId, ct)).Single(x => x.Id == id));
    }

    public async Task<RealEstateMutationOutcome<PropertyImprovementView>> UpdateImprovementAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid improvementId, PropertyImprovementWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (ValidateImprovement(request) is { } error) return new(RealEstateMutationResult.Invalid, Error: error);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "PropertyImprovements"
SET "Title"={request.Title.Trim()},"Category"={request.Category.Trim().ToLowerInvariant()},"StartDate"={request.StartDate},"CompletedDate"={request.CompletedDate},
    "Cost"={request.Cost},"Currency"={NormalizeCurrency(request.Currency)},"EstimatedValueAdded"={request.EstimatedValueAdded},"Description"={Trim(request.Description)},"UpdatedAt"=now()
WHERE "Id"={improvementId} AND "AssetId"={assetId};
""", ct);
        if (affected == 0) return new(RealEstateMutationResult.NotFound);
        audit.Record(fullWorthSpaceId, userId, "property.improvement.updated", "PropertyImprovement", improvementId);
        await db.SaveChangesAsync(ct);
        return new(RealEstateMutationResult.Success, (await ReadImprovementsAsync(assetId, ct)).Single(x => x.Id == improvementId));
    }

    public async Task<RealEstateMutationResult> DeleteImprovementAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid improvementId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return access;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"PropertyImprovements\" WHERE \"Id\"={improvementId} AND \"AssetId\"={assetId};", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "property.improvement.deleted", "PropertyImprovement", improvementId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    public async Task<RealEstateMutationResult> LinkImprovementCashflowAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid improvementId, Guid cashflowId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return access;
        if (!await ImprovementExistsAsync(assetId, improvementId, ct)) return RealEstateMutationResult.NotFound;
        if (!await CashflowCanLinkAsync(fullWorthSpaceId, assetId, cashflowId, ct)) return RealEstateMutationResult.Invalid;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "PropertyImprovementCashflows" ("ImprovementId","CashflowEntryId") VALUES ({improvementId},{cashflowId})
ON CONFLICT ("ImprovementId","CashflowEntryId") DO NOTHING;
""", ct);
        audit.Record(fullWorthSpaceId, userId, "property.improvement.cashflow_linked", "PropertyImprovement", improvementId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    public async Task<RealEstateMutationResult> UnlinkImprovementCashflowAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid improvementId, Guid cashflowId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return access;
        if (!await ImprovementExistsAsync(assetId, improvementId, ct)) return RealEstateMutationResult.NotFound;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
DELETE FROM "PropertyImprovementCashflows" WHERE "ImprovementId"={improvementId} AND "CashflowEntryId"={cashflowId};
""", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "property.improvement.cashflow_unlinked", "PropertyImprovement", improvementId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    public async Task<RealEstateMutationOutcome<IReadOnlyList<AssetRecurringContractLinkView>>> ListContractLinksAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadPropertyAsync(userId, fullWorthSpaceId, assetId, ct)) return new(RealEstateMutationResult.NotFound);
        return new(RealEstateMutationResult.Success, await ReadContractLinksAsync(userId, fullWorthSpaceId, assetId, ct));
    }

    public async Task<RealEstateMutationOutcome<AssetRecurringContractLinkView>> CreateContractLinkAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, AssetRecurringContractLinkWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        var role = request.Role?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RealEstateOperationsKinds.ContractRoles.Contains(role)) return new(RealEstateMutationResult.Invalid, Error: "Unsupported property contract role.");
        if (!await AccessibleContractExistsAsync(userId, fullWorthSpaceId, request.RecurringContractId, ct))
            return new(RealEstateMutationResult.Invalid, Error: "Recurring contract is not accessible in this FullWorth Space.");

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "AssetRecurringContractLinks" ("FullWorthSpaceId","AssetId","RecurringContractId","Role","CreatedAt")
VALUES ({fullWorthSpaceId},{assetId},{request.RecurringContractId},{role},now())
ON CONFLICT ("AssetId","RecurringContractId") DO UPDATE SET "Role"=EXCLUDED."Role";
""", ct);
        _ = affected;
        audit.Record(fullWorthSpaceId, userId, "property.contract_link.updated", "RecurringContract", request.RecurringContractId);
        await db.SaveChangesAsync(ct);
        var linked = (await ReadContractLinksAsync(userId, fullWorthSpaceId, assetId, ct)).Single(x => x.RecurringContractId == request.RecurringContractId);
        return new(RealEstateMutationResult.Success, linked);
    }

    public async Task<RealEstateMutationResult> DeleteContractLinkAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid contractId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return access;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
DELETE FROM "AssetRecurringContractLinks" WHERE "FullWorthSpaceId"={fullWorthSpaceId} AND "AssetId"={assetId} AND "RecurringContractId"={contractId};
""", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "property.contract_link.deleted", "RecurringContract", contractId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    internal async Task<List<PropertyImprovementView>> ReadImprovementsAsync(Guid assetId, CancellationToken ct)
    {
        var result = new List<PropertyImprovementView>();
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open; if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT i."Id",i."AssetId",i."Title",i."Category",i."StartDate",i."CompletedDate",i."Cost",i."Currency",i."EstimatedValueAdded",i."Description",i."DocumentId",i."CreatedAt",i."UpdatedAt"
FROM "PropertyImprovements" i WHERE i."AssetId"=@asset ORDER BY COALESCE(i."CompletedDate",i."StartDate") DESC NULLS LAST,i."CreatedAt" DESC;
""";
            AddParameter(command, "@asset", assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var raw = new List<(Guid Id, Guid AssetId, string Title, string Category, DateOnly? Start, DateOnly? Completed, decimal? Cost, string? Currency, decimal? Added, string? Description, Guid? Document, DateTimeOffset Created, DateTimeOffset Updated)>();
            while (await reader.ReadAsync(ct))
                raw.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), DateOrNull(reader,4), DateOrNull(reader,5), DecimalOrNull(reader,6), StringOrNull(reader,7), DecimalOrNull(reader,8), StringOrNull(reader,9), reader.IsDBNull(10)?null:reader.GetGuid(10), reader.GetFieldValue<DateTimeOffset>(11), reader.GetFieldValue<DateTimeOffset>(12)));
            await reader.CloseAsync();
            foreach (var item in raw)
            {
                await using var links = connection.CreateCommand();
                links.CommandText = "SELECT \"CashflowEntryId\" FROM \"PropertyImprovementCashflows\" WHERE \"ImprovementId\"=@id ORDER BY \"CashflowEntryId\";";
                AddParameter(links, "@id", item.Id);
                var ids = new List<Guid>(); await using var linkReader = await links.ExecuteReaderAsync(ct); while (await linkReader.ReadAsync(ct)) ids.Add(linkReader.GetGuid(0));
                result.Add(new PropertyImprovementView(item.Id,item.AssetId,item.Title,item.Category,item.Start,item.Completed,item.Cost,item.Currency,item.Added,item.Description,item.Document,ids,item.Created,item.Updated));
            }
            return result;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    internal async Task<List<AssetRecurringContractLinkView>> ReadContractLinksAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var result = new List<AssetRecurringContractLinkView>();
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open; if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT l."AssetId",l."RecurringContractId",l."Role",c."Name",c."Amount",c."Currency",c."BillingCycle",c."IsActive",c."NextDueDate",l."CreatedAt"
FROM "AssetRecurringContractLinks" l JOIN "Contracts" c ON c."Id"=l."RecurringContractId"
WHERE l."FullWorthSpaceId"=@space AND l."AssetId"=@asset
  AND (c."AccountId" IS NULL OR EXISTS (SELECT 1 FROM "AccountOwners" o WHERE o."AccountId"=c."AccountId" AND o."UserId"=@user))
ORDER BY l."Role",c."Name";
""";
            AddParameter(command,"@space",fullWorthSpaceId); AddParameter(command,"@asset",assetId); AddParameter(command,"@user",userId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new AssetRecurringContractLinkView(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetDecimal(4),reader.GetString(5),reader.GetString(6),reader.GetBoolean(7),DateOrNull(reader,8),reader.GetFieldValue<DateTimeOffset>(9)));
            return result;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private async Task<bool> CanReadPropertyAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct) =>
        await db.Assets.AsNoTracking().AnyAsync(asset => asset.Id==assetId && asset.FullWorthSpaceId==fullWorthSpaceId && asset.Kind==AssetKinds.RealEstate && db.FullWorthSpaceMembers.Any(member=>member.FullWorthSpaceId==fullWorthSpaceId&&member.UserId==userId),ct);

    private async Task<RealEstateMutationResult> WriteAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadPropertyAsync(userId,fullWorthSpaceId,assetId,ct)) return RealEstateMutationResult.NotFound;
        return await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member=>member.FullWorthSpaceId==fullWorthSpaceId&&member.UserId==userId&&member.Role==FullWorthSpaceRoles.Owner,ct) ? RealEstateMutationResult.Success : RealEstateMutationResult.Forbidden;
    }

    private Task<bool> ImprovementExistsAsync(Guid assetId, Guid improvementId, CancellationToken ct) => ScalarExistsAsync("SELECT 1 FROM \"PropertyImprovements\" WHERE \"Id\"=@id AND \"AssetId\"=@asset;",ct,("@id",improvementId),("@asset",assetId));
    private Task<bool> CashflowCanLinkAsync(Guid fullWorthSpaceId, Guid assetId, Guid cashflowId, CancellationToken ct) => ScalarExistsAsync("SELECT 1 FROM \"AssetCashflowEntries\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"AssetId\"=@asset AND \"IsPlanned\"=false AND \"Direction\"='expense';",ct,("@id",cashflowId),("@space",fullWorthSpaceId),("@asset",assetId));

    private async Task<bool> AccessibleContractExistsAsync(Guid userId, Guid fullWorthSpaceId, Guid contractId, CancellationToken ct) =>
        await db.Contracts.AsNoTracking().AnyAsync(contract=>contract.Id==contractId&&contract.FullWorthSpaceId==fullWorthSpaceId&&db.FullWorthSpaceMembers.Any(member=>member.FullWorthSpaceId==fullWorthSpaceId&&member.UserId==userId)&&(!contract.AccountId.HasValue||db.AccountOwners.Any(owner=>owner.AccountId==contract.AccountId.Value&&owner.UserId==userId)),ct);

    private static string? ValidateImprovement(PropertyImprovementWrite request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length>200) return "Improvement title is required and limited to 200 characters.";
        if (!RealEstateOperationsKinds.ImprovementCategories.Contains(request.Category?.Trim().ToLowerInvariant()??string.Empty)) return "Unsupported improvement category.";
        if (request.CompletedDate.HasValue&&request.StartDate.HasValue&&request.CompletedDate.Value<request.StartDate.Value) return "Improvement completion cannot be before start.";
        if (request.Cost is <0m||request.EstimatedValueAdded is <0m) return "Improvement amounts cannot be negative.";
        if (request.Currency is not null&&!RealEstateValidation.ValidCurrency(request.Currency)) return "Currency must contain three letters.";
        if (request.Cost.HasValue&&string.IsNullOrWhiteSpace(request.Currency)) return "Currency is required when cost is set.";
        if (request.DocumentId.HasValue) return "Document links are introduced in the property documents PR.";
        return null;
    }

    private async Task<bool> ScalarExistsAsync(string sql,CancellationToken ct,params (string Name,object? Value)[] parameters)
    {
        var connection=db.Database.GetDbConnection();var close=connection.State!=ConnectionState.Open;if(close)await connection.OpenAsync(ct);
        try{await using var command=connection.CreateCommand();command.CommandText=sql;foreach(var p in parameters)AddParameter(command,p.Name,p.Value);return await command.ExecuteScalarAsync(ct)is not null;}
        finally{if(close)await connection.CloseAsync();}
    }

    private static string? NormalizeCurrency(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim().ToUpperInvariant();
    private static string? Trim(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private static decimal? DecimalOrNull(DbDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:reader.GetDecimal(ordinal);
    private static DateOnly? DateOrNull(DbDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:reader.GetFieldValue<DateOnly>(ordinal);
    private static string? StringOrNull(DbDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:reader.GetString(ordinal);
    private static void AddParameter(DbCommand command,string name,object? value){var p=command.CreateParameter();p.ParameterName=name;p.Value=value??DBNull.Value;command.Parameters.Add(p);}
}
