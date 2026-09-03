using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class AssetCashflowStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<RealEstateMutationOutcome<IReadOnlyList<AssetCashflowView>>> ListAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await CanReadAssetAsync(userId, fullWorthSpaceId, assetId, ct)) return new(RealEstateMutationResult.NotFound);
        return new(RealEstateMutationResult.Success, await ReadAsync(userId, fullWorthSpaceId, assetId, from, to, ct));
    }

    public async Task<RealEstateMutationOutcome<AssetCashflowView>> CreateAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, AssetCashflowWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        var normalized = await NormalizeAndValidateAsync(userId, fullWorthSpaceId, assetId, null, request, ct);
        if (normalized.Error is not null) return new(RealEstateMutationResult.Invalid, Error: normalized.Error);

        var id = Guid.NewGuid();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
INSERT INTO "AssetCashflowEntries" ("Id","FullWorthSpaceId","AssetId","TransactionId","Date","Type","Amount","Direction","Currency","IsPlanned","Notes","CreatedAt","UpdatedAt")
VALUES ({id},{fullWorthSpaceId},{assetId},{request.TransactionId},{normalized.Date},{normalized.Type},{request.Amount},{normalized.Direction},{normalized.Currency},{normalized.IsPlanned},{Trim(request.Notes)},now(),now());
""", ct);
        }
        catch (PostgresException) { return new(RealEstateMutationResult.Invalid, Error: "Cashflow violates transaction allocation constraints."); }

        audit.Record(fullWorthSpaceId, userId, "asset.cashflow.created", "AssetCashflowEntry", id);
        await db.SaveChangesAsync(ct);
        var created = (await ReadAsync(userId, fullWorthSpaceId, assetId, null, null, ct)).Single(x => x.Id == id);
        return new(RealEstateMutationResult.Success, created);
    }

    public async Task<RealEstateMutationOutcome<AssetCashflowView>> UpdateAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid entryId, AssetCashflowWrite request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return new(access);
        if (!await EntryExistsAsync(fullWorthSpaceId, assetId, entryId, ct)) return new(RealEstateMutationResult.NotFound);
        var normalized = await NormalizeAndValidateAsync(userId, fullWorthSpaceId, assetId, entryId, request, ct);
        if (normalized.Error is not null) return new(RealEstateMutationResult.Invalid, Error: normalized.Error);

        int affected;
        try
        {
            affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
UPDATE "AssetCashflowEntries"
SET "TransactionId"={request.TransactionId}, "Date"={normalized.Date}, "Type"={normalized.Type}, "Amount"={request.Amount},
    "Direction"={normalized.Direction}, "Currency"={normalized.Currency}, "IsPlanned"={normalized.IsPlanned}, "Notes"={Trim(request.Notes)}, "UpdatedAt"=now()
WHERE "Id"={entryId} AND "FullWorthSpaceId"={fullWorthSpaceId} AND "AssetId"={assetId};
""", ct);
        }
        catch (PostgresException) { return new(RealEstateMutationResult.Invalid, Error: "Cashflow violates transaction allocation constraints."); }
        if (affected == 0) return new(RealEstateMutationResult.NotFound);

        audit.Record(fullWorthSpaceId, userId, "asset.cashflow.updated", "AssetCashflowEntry", entryId);
        await db.SaveChangesAsync(ct);
        var updated = (await ReadAsync(userId, fullWorthSpaceId, assetId, null, null, ct)).Single(x => x.Id == entryId);
        return new(RealEstateMutationResult.Success, updated);
    }

    public async Task<RealEstateMutationResult> DeleteAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid entryId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != RealEstateMutationResult.Success) return access;
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
DELETE FROM "AssetCashflowEntries" WHERE "Id"={entryId} AND "FullWorthSpaceId"={fullWorthSpaceId} AND "AssetId"={assetId};
""", ct);
        if (affected == 0) return RealEstateMutationResult.NotFound;
        audit.Record(fullWorthSpaceId, userId, "asset.cashflow.deleted", "AssetCashflowEntry", entryId);
        await db.SaveChangesAsync(ct);
        return RealEstateMutationResult.Success;
    }

    internal async Task<List<AssetCashflowView>> ReadAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var result = new List<AssetCashflowView>();
        var connection = db.Database.GetDbConnection();
        var close = connection.State != ConnectionState.Open;
        if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT c."Id",c."FullWorthSpaceId",c."AssetId",
       CASE WHEN c."TransactionId" IS NOT NULL AND EXISTS (
           SELECT 1 FROM "AccountOwners" o WHERE o."AccountId"=t."AccountId" AND o."UserId"=@user
       ) THEN c."TransactionId" ELSE NULL END AS "VisibleTransactionId",
       c."Date",c."Type",c."Amount",c."Direction",c."Currency",c."IsPlanned",c."Notes",
       CASE WHEN c."TransactionId" IS NOT NULL AND EXISTS (
           SELECT 1 FROM "AccountOwners" o WHERE o."AccountId"=t."AccountId" AND o."UserId"=@user
       ) THEN t."Counterparty" ELSE NULL END AS "VisibleCounterparty",
       c."CreatedAt",c."UpdatedAt"
FROM "AssetCashflowEntries" c LEFT JOIN "Transactions" t ON t."Id"=c."TransactionId"
WHERE c."FullWorthSpaceId"=@space AND c."AssetId"=@asset AND (@from::date IS NULL OR c."Date">=@from) AND (@to::date IS NULL OR c."Date"<=@to)
ORDER BY c."Date" DESC,c."CreatedAt" DESC,c."Id";
""";
            AddParameter(command, "@user", userId); AddParameter(command, "@space", fullWorthSpaceId); AddParameter(command, "@asset", assetId); AddParameter(command, "@from", from); AddParameter(command, "@to", to);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new AssetCashflowView(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.GetFieldValue<DateOnly>(4), reader.GetString(5), reader.GetDecimal(6), reader.GetString(7), reader.GetString(8), reader.GetBoolean(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetFieldValue<DateTimeOffset>(12), reader.GetFieldValue<DateTimeOffset>(13)));
            }
            return result;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private async Task<(string? Error, DateOnly Date, string Type, string Direction, string Currency, bool IsPlanned)> NormalizeAndValidateAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, Guid? existingId, AssetCashflowWrite request, CancellationToken ct)
    {
        var type = request.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RealEstateOperationsKinds.CashflowTypes.Contains(type)) return ("Unsupported cashflow type.", default, type, string.Empty, string.Empty, false);
        if (request.Amount <= 0m) return ("Cashflow amount must be greater than zero.", default, type, string.Empty, string.Empty, false);

        if (request.TransactionId is { } transactionId)
        {
            if (request.IsPlanned) return ("Transaction-backed cashflows cannot be planned.", default, type, string.Empty, string.Empty, false);
            var transaction = await db.Transactions.AsNoTracking()
                .Where(tx => tx.Id == transactionId)
                .Join(db.Accounts.AsNoTracking(), tx => tx.AccountId, account => account.Id, (tx, account) => new { tx, account })
                .Where(x => x.account.FullWorthSpaceId == fullWorthSpaceId &&
                            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
                            db.AccountOwners.Any(owner => owner.AccountId == x.account.Id && owner.UserId == userId))
                .Select(x => new { x.tx.Amount, x.tx.Currency, x.tx.BookingDate, x.tx.ValueDate })
                .SingleOrDefaultAsync(ct);
            if (transaction is null) return ("Linked transaction is not accessible in this FullWorth Space.", default, type, string.Empty, string.Empty, false);
            if (transaction.Amount == 0m) return ("Zero-value transactions cannot back an asset cashflow.", default, type, string.Empty, string.Empty, false);

            var direction = transaction.Amount > 0m ? "income" : "expense";
            if (!DirectionMatchesType(type, direction))
                return ("Cashflow type does not match the linked transaction direction.", default, type, direction, string.Empty, false);

            var alreadyAllocated = await SumTransactionAllocationsAsync(transactionId, existingId, ct);
            if (alreadyAllocated + request.Amount > Math.Abs(transaction.Amount))
                return ("Cashflow allocations cannot exceed the transaction amount.", default, type, string.Empty, string.Empty, false);
            var date = transaction.BookingDate ?? transaction.ValueDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return (null, date, type, direction, transaction.Currency.Trim().ToUpperInvariant(), false);
        }

        var manualDirection = request.Direction?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!RealEstateOperationsKinds.Directions.Contains(manualDirection)) return ("Direction must be income or expense.", default, type, manualDirection, string.Empty, request.IsPlanned);
        if (!DirectionMatchesType(type, manualDirection)) return ("Cashflow type does not match the selected direction.", default, type, manualDirection, string.Empty, request.IsPlanned);
        if (!request.Date.HasValue) return ("Manual cashflows require a date.", default, type, manualDirection, string.Empty, request.IsPlanned);
        if (!RealEstateValidation.ValidCurrency(request.Currency)) return ("Manual cashflows require a three-letter currency.", default, type, manualDirection, string.Empty, request.IsPlanned);
        return (null, request.Date.Value, type, manualDirection, request.Currency!.Trim().ToUpperInvariant(), request.IsPlanned);
    }

    private static bool DirectionMatchesType(string type, string direction) => type switch
    {
        "rental_income" => direction == "income",
        "operating_expense" or "capex" or "debt_payment" or "tax" or "insurance" or "fee" => direction == "expense",
        _ => true
    };

    private async Task<decimal> SumTransactionAllocationsAsync(Guid transactionId, Guid? existingId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open; if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(\"Amount\"),0) FROM \"AssetCashflowEntries\" WHERE \"TransactionId\"=@transaction AND (@id::uuid IS NULL OR \"Id\"<>@id);";
            AddParameter(command, "@transaction", transactionId); AddParameter(command, "@id", existingId);
            return Convert.ToDecimal(await command.ExecuteScalarAsync(ct));
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private async Task<bool> CanReadAssetAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct) =>
        await db.Assets.AsNoTracking().AnyAsync(asset => asset.Id == assetId && asset.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId), ct);

    private async Task<RealEstateMutationResult> WriteAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await CanReadAssetAsync(userId, fullWorthSpaceId, assetId, ct)) return RealEstateMutationResult.NotFound;
        return await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId && member.Role == FullWorthSpaceRoles.Owner, ct)
            ? RealEstateMutationResult.Success : RealEstateMutationResult.Forbidden;
    }

    private async Task<bool> EntryExistsAsync(Guid fullWorthSpaceId, Guid assetId, Guid entryId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection(); var close = connection.State != ConnectionState.Open; if (close) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT 1 FROM \"AssetCashflowEntries\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"AssetId\"=@asset;";
            AddParameter(command, "@id", entryId); AddParameter(command, "@space", fullWorthSpaceId); AddParameter(command, "@asset", assetId);
            return await command.ExecuteScalarAsync(ct) is not null;
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void AddParameter(DbCommand command, string name, object? value) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; command.Parameters.Add(parameter); }
}
