using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed class ReceivablePaymentStore(
    FullWorthDbContext db,
    RemainingAssetStore assets,
    AuditService audit)
{
    public async Task<SpecializedAssetOutcome<IReadOnlyList<ReceivablePaymentView>>> ListAsync(
        Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        if (!await assets.CanReadReceivableAsync(userId, fullWorthSpaceId, assetId, ct))
            return new(SpecializedAssetMutationResult.NotFound);

        var rows = new List<ReceivablePaymentView>();
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p."Id", p."AssetId",
                       CASE WHEN p."TransactionId" IS NOT NULL AND EXISTS (
                           SELECT 1 FROM "Transactions" t
                           JOIN "AccountOwners" o ON o."AccountId"=t."AccountId"
                           WHERE t."Id"=p."TransactionId" AND o."UserId"=@user
                       ) THEN p."TransactionId" ELSE NULL END AS "VisibleTransactionId",
                       p."Date", p."PrincipalAmount", p."InterestAmount", p."Currency",
                       p."Notes", p."CreatedByUserId", p."CreatedAt"
                FROM "ReceivablePayments" p
                WHERE p."FullWorthSpaceId"=@space AND p."AssetId"=@asset
                ORDER BY p."Date" DESC, p."CreatedAt" DESC, p."Id" DESC;
                """;
            AddParameter(command, "@user", userId);
            AddParameter(command, "@space", fullWorthSpaceId);
            AddParameter(command, "@asset", assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(ReadPayment(reader));
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
        return new(SpecializedAssetMutationResult.Success, rows);
    }

    public async Task<SpecializedAssetOutcome<ReceivableMutationView>> CreateAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid assetId,
        ReceivablePaymentWrite request,
        CancellationToken ct)
    {
        var access = await assets.ReceivableWriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != SpecializedAssetMutationResult.Success) return new(access);

        var currency = NormalizeCurrency(request.Currency);
        if (request.PrincipalAmount < 0m || request.InterestAmount < 0m || (request.PrincipalAmount == 0m && request.InterestAmount == 0m))
            return new(SpecializedAssetMutationResult.Invalid, Error: "Principal and interest must be non-negative and at least one amount must be positive.");
        if (!ValidCurrency(currency)) return new(SpecializedAssetMutationResult.Invalid, Error: "Payment currency must be a three-letter code.");
        if (request.Notes?.Trim().Length > 1000) return new(SpecializedAssetMutationResult.Invalid, Error: "Payment notes are too long.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var locked = await LockReceivableAsync(fullWorthSpaceId, assetId, ct);
            if (locked is null)
            {
                await transaction.RollbackAsync(ct);
                return new(SpecializedAssetMutationResult.NotFound);
            }
            if (locked.Status == "written_off")
            {
                await transaction.RollbackAsync(ct);
                return new(SpecializedAssetMutationResult.Conflict, Error: "A written-off receivable must be reactivated before recording repayments.");
            }
            if (!string.Equals(currency, locked.Currency, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(ct);
                return new(SpecializedAssetMutationResult.Invalid, Error: "Payment currency must match the receivable currency.");
            }
            if (request.PrincipalAmount > locked.OutstandingPrincipal)
            {
                await transaction.RollbackAsync(ct);
                return new(SpecializedAssetMutationResult.Invalid, Error: "Principal repayment cannot exceed outstanding principal.");
            }
            if (request.PrincipalAmount > 0m && !string.Equals(locked.AssetCurrency, locked.Currency, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(ct);
                return new(SpecializedAssetMutationResult.Conflict, Error: "Accepted asset value must use the receivable currency before principal can be reduced automatically.");
            }

            if (request.TransactionId.HasValue)
            {
                // Lock the canonical bank transaction so concurrent receivable allocations serialize.
                var linked = await LockAccessibleTransactionAsync(userId, fullWorthSpaceId, request.TransactionId.Value, ct);
                if (linked is null)
                {
                    await transaction.RollbackAsync(ct);
                    return new(SpecializedAssetMutationResult.NotFound, Error: "Linked transaction is not accessible in this FullWorth Space.");
                }
                if (linked.Amount <= 0m || !string.Equals(linked.Currency, locked.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync(ct);
                    return new(SpecializedAssetMutationResult.Invalid, Error: "Linked transaction must be positive and use the receivable currency.");
                }
                if (await PaymentTransactionExistsAsync(assetId, request.TransactionId.Value, ct))
                {
                    await transaction.RollbackAsync(ct);
                    return new(SpecializedAssetMutationResult.Conflict, Error: "This transaction is already linked to a payment for the receivable.");
                }

                var requestedAllocation = request.PrincipalAmount + request.InterestAmount;
                var alreadyAllocated = await SumTransactionAllocationsAsync(request.TransactionId.Value, ct);
                if (alreadyAllocated + requestedAllocation > linked.Amount + 0.01m)
                {
                    await transaction.RollbackAsync(ct);
                    return new(SpecializedAssetMutationResult.Invalid, Error: "Receivable payment allocations cannot exceed the linked transaction amount.");
                }
            }

            var paymentId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "ReceivablePayments"
                    ("Id", "FullWorthSpaceId", "AssetId", "TransactionId", "Date", "PrincipalAmount", "InterestAmount",
                     "Currency", "Notes", "CreatedByUserId", "CreatedAt")
                VALUES
                    ({paymentId}, {fullWorthSpaceId}, {assetId}, {request.TransactionId}, {request.Date}, {request.PrincipalAmount},
                     {request.InterestAmount}, {currency}, {Trim(request.Notes)}, {userId}, now());
                """, ct);

            var remaining = locked.OutstandingPrincipal - request.PrincipalAmount;
            var nextStatus = remaining == 0m ? "settled" : locked.Status == "settled" ? "active" : locked.Status;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "ReceivableAssetDetails"
                SET "OutstandingPrincipal"={remaining}, "Status"={nextStatus}, "UpdatedAt"=now()
                WHERE "AssetId"={assetId};
                """, ct);

            // Principal changes legal outstanding principal and the accepted asset-value cache.
            // Interest is income only and deliberately does not reduce the asset value.
            if (request.PrincipalAmount > 0m)
            {
                var acceptedValue = Math.Max(0m, locked.AssetCurrentValue - request.PrincipalAmount);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "Assets"
                    SET "CurrentValue"={acceptedValue}, "Currency"={locked.Currency}, "ValuedAt"={request.Date}, "UpdatedAt"=now()
                    WHERE "Id"={assetId} AND "FullWorthSpaceId"={fullWorthSpaceId};
                    """, ct);
            }

            audit.Record(fullWorthSpaceId, userId, "asset.receivable.payment.created", "ReceivablePayment", paymentId);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return await MutationViewAsync(fullWorthSpaceId, assetId, ct);
    }

    public async Task<SpecializedAssetOutcome<ReceivableMutationView>> WriteDownAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid assetId,
        ReceivableWriteDownRequest request,
        CancellationToken ct)
    {
        var access = await assets.ReceivableWriteAccessAsync(userId, fullWorthSpaceId, assetId, ct);
        if (access != SpecializedAssetMutationResult.Success) return new(access);
        if (!request.Confirmed) return new(SpecializedAssetMutationResult.Invalid, Error: "Explicit confirmation is required for a receivable write-down.");
        if (request.RecoverableAmount < 0m) return new(SpecializedAssetMutationResult.Invalid, Error: "Recoverable amount cannot be negative.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var locked = await LockReceivableAsync(fullWorthSpaceId, assetId, ct);
            if (locked is null)
            {
                await transaction.RollbackAsync(ct);
                return new(SpecializedAssetMutationResult.NotFound);
            }
            if (locked.OutstandingPrincipal <= 0m)
            {
                await transaction.RollbackAsync(ct);
                return new(SpecializedAssetMutationResult.Conflict, Error: "A settled receivable cannot be written down.");
            }
            if (request.RecoverableAmount > locked.OutstandingPrincipal)
            {
                await transaction.RollbackAsync(ct);
                return new(SpecializedAssetMutationResult.Invalid, Error: "Recoverable amount cannot exceed outstanding principal.");
            }

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "ReceivableAssetDetails"
                SET "Status"='written_off', "UpdatedAt"=now()
                WHERE "AssetId"={assetId};
                UPDATE "Assets"
                SET "CurrentValue"={request.RecoverableAmount}, "Currency"={locked.Currency}, "ValuedAt"={today}, "UpdatedAt"=now()
                WHERE "Id"={assetId} AND "FullWorthSpaceId"={fullWorthSpaceId};
                """, ct);

            audit.Record(fullWorthSpaceId, userId, "asset.receivable.written_off", "Asset", assetId);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return await MutationViewAsync(fullWorthSpaceId, assetId, ct);
    }

    private async Task<SpecializedAssetOutcome<ReceivableMutationView>> MutationViewAsync(Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var detail = await assets.ReadReceivableForMutationAsync(assetId, ct);
        var asset = await db.Assets.AsNoTracking()
            .Where(x => x.Id == assetId && x.FullWorthSpaceId == fullWorthSpaceId)
            .Select(x => new { x.CurrentValue, x.Currency, x.ValuedAt })
            .SingleOrDefaultAsync(ct);
        return detail is null || asset is null
            ? new(SpecializedAssetMutationResult.NotFound)
            : new(SpecializedAssetMutationResult.Success, new ReceivableMutationView(detail, asset.CurrentValue, asset.Currency, asset.ValuedAt));
    }

    private async Task<LockedReceivable?> LockReceivableAsync(Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            SELECT d."OriginalPrincipal", d."OutstandingPrincipal", d."Currency", d."Status",
                   a."CurrentValue", a."Currency"
            FROM "ReceivableAssetDetails" d
            JOIN "Assets" a ON a."Id"=d."AssetId"
            WHERE d."AssetId"=@asset AND a."FullWorthSpaceId"=@space
            FOR UPDATE OF d, a;
            """;
        AddParameter(command, "@asset", assetId);
        AddParameter(command, "@space", fullWorthSpaceId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new LockedReceivable(
            reader.GetDecimal(0),
            reader.GetDecimal(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetDecimal(4),
            reader.GetString(5));
    }

    private async Task<LockedTransaction?> LockAccessibleTransactionAsync(
        Guid userId, Guid fullWorthSpaceId, Guid transactionId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            SELECT t."Amount", t."Currency"
            FROM "Transactions" t
            JOIN "Accounts" a ON a."Id"=t."AccountId"
            WHERE t."Id"=@transaction
              AND a."FullWorthSpaceId"=@space
              AND EXISTS (SELECT 1 FROM "AccountOwners" o WHERE o."AccountId"=a."Id" AND o."UserId"=@user)
            FOR UPDATE OF t;
            """;
        AddParameter(command, "@transaction", transactionId);
        AddParameter(command, "@space", fullWorthSpaceId);
        AddParameter(command, "@user", userId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new LockedTransaction(reader.GetDecimal(0), reader.GetString(1));
    }

    private async Task<decimal> SumTransactionAllocationsAsync(Guid transactionId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            SELECT COALESCE(SUM("PrincipalAmount" + "InterestAmount"), 0)
            FROM "ReceivablePayments"
            WHERE "TransactionId"=@transaction;
            """;
        AddParameter(command, "@transaction", transactionId);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(ct));
    }

    private async Task<bool> PaymentTransactionExistsAsync(Guid assetId, Guid transactionId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT 1 FROM \"ReceivablePayments\" WHERE \"AssetId\"=@asset AND \"TransactionId\"=@transaction LIMIT 1;";
        AddParameter(command, "@asset", assetId);
        AddParameter(command, "@transaction", transactionId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static ReceivablePaymentView ReadPayment(DbDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.IsDBNull(2) ? null : reader.GetGuid(2),
        reader.GetFieldValue<DateOnly>(3),
        reader.GetDecimal(4),
        reader.GetDecimal(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetGuid(8),
        reader.GetFieldValue<DateTimeOffset>(9));

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeCurrency(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(char.IsAsciiLetterUpper);

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record LockedReceivable(
        decimal OriginalPrincipal,
        decimal OutstandingPrincipal,
        string Currency,
        string Status,
        decimal AssetCurrentValue,
        string AssetCurrency);

    private sealed record LockedTransaction(decimal Amount, string Currency);
}
