using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed record AssetValuationView(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid AssetId,
    decimal Amount,
    string Currency,
    DateOnly ValuedAt,
    string Method,
    decimal? LowEstimate,
    decimal? HighEstimate,
    decimal? Confidence,
    string? ProviderKey,
    string? ProviderDisplayName,
    string? ExternalReference,
    bool IsCurrent,
    bool IsAccepted,
    Guid? CreatedByUserId,
    DateTimeOffset CreatedAt);

public sealed record AssetValuationWrite(
    decimal Amount,
    string Currency,
    DateOnly? ValuedAt = null,
    string? Method = null,
    decimal? LowEstimate = null,
    decimal? HighEstimate = null,
    decimal? Confidence = null,
    string? ProviderKey = null,
    string? ProviderDisplayName = null,
    string? ExternalReference = null,
    bool IsAccepted = true);

public enum AssetValuationMutationResult
{
    Success,
    NotFound,
    Forbidden,
    Invalid
}

public sealed record AssetValuationMutationOutcome(
    AssetValuationMutationResult Result,
    AssetValuationView? Valuation = null,
    string? Error = null);

public static class AssetKinds
{
    public const string RealEstate = "real_estate";
    public const string Vehicle = "vehicle";
    public const string PreciousMetal = "precious_metal";
    public const string Collectible = "collectible";
    public const string Receivable = "receivable";
    public const string BusinessInterest = "business_interest";
    public const string InsurancePension = "insurance_pension";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        RealEstate,
        Vehicle,
        PreciousMetal,
        Collectible,
        Receivable,
        BusinessInterest,
        InsurancePension,
        Other
    };
}

public sealed class AssetValuationStore(FullWorthDbContext db, AuditService audit)
{
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        "manual",
        "purchase_price",
        "internal_estimate",
        "external_provider",
        "appraisal",
        "import",
        "legacy"
    };

    public async Task<IReadOnlyList<AssetValuationView>?> ListForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid assetId,
        CancellationToken ct)
    {
        if (!await AssetVisibleAsync(userId, fullWorthSpaceId, assetId, ct))
            return null;

        return await ReadHistoryAsync(fullWorthSpaceId, assetId, ct);
    }

    public async Task<AssetValuationMutationOutcome> CreateForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid assetId,
        AssetValuationWrite request,
        CancellationToken ct)
    {
        if (!await AssetVisibleAsync(userId, fullWorthSpaceId, assetId, ct))
            return new(AssetValuationMutationResult.NotFound);

        var role = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId)
            .Select(member => member.Role)
            .SingleOrDefaultAsync(ct);
        if (role != FullWorthSpaceRoles.Owner)
            return new(AssetValuationMutationResult.Forbidden);

        var normalized = NormalizeAndValidate(request);
        if (normalized.Error is not null)
            return new(AssetValuationMutationResult.Invalid, Error: normalized.Error);

        var value = normalized.Value;
        var valuationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (!await LockAssetAsync(fullWorthSpaceId, assetId, ct))
            {
                await transaction.RollbackAsync(ct);
                return new(AssetValuationMutationResult.NotFound);
            }

            if (request.IsAccepted)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "SET LOCAL fullworth.asset_valuation_suppress = 'on';",
                    ct);

                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "AssetValuations"
                    SET "IsCurrent" = FALSE
                    WHERE "AssetId" = {assetId} AND "FullWorthSpaceId" = {fullWorthSpaceId} AND "IsCurrent" = TRUE;
                    """, ct);

                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "Assets"
                    SET "CurrentValue" = {value.Amount},
                        "Currency" = {value.Currency},
                        "ValuedAt" = {value.ValuedAt},
                        "UpdatedAt" = {now}
                    WHERE "Id" = {assetId} AND "FullWorthSpaceId" = {fullWorthSpaceId};
                    """, ct);
            }

            await InsertAsync(
                valuationId,
                fullWorthSpaceId,
                assetId,
                value,
                request.IsAccepted,
                request.IsAccepted,
                userId,
                now,
                ct);

            audit.Record(
                fullWorthSpaceId,
                userId,
                request.IsAccepted ? "asset.valuation.accepted" : "asset.valuation.created",
                "AssetValuation",
                valuationId);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        var created = await ReadOneAsync(fullWorthSpaceId, assetId, valuationId, ct);
        return created is null
            ? new(AssetValuationMutationResult.NotFound)
            : new(AssetValuationMutationResult.Success, created);
    }

    private async Task<bool> AssetVisibleAsync(Guid userId, Guid fullWorthSpaceId, Guid assetId, CancellationToken ct) =>
        await db.Assets.AsNoTracking().AnyAsync(asset =>
            asset.Id == assetId &&
            asset.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member =>
                member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId), ct);

    private async Task<bool> LockAssetAsync(Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT 1 FROM \"Assets\" WHERE \"Id\"=@asset AND \"FullWorthSpaceId\"=@space FOR UPDATE;";
        AddParameter(command, "@asset", assetId);
        AddParameter(command, "@space", fullWorthSpaceId);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private async Task InsertAsync(
        Guid id,
        Guid fullWorthSpaceId,
        Guid assetId,
        NormalizedValuation value,
        bool isCurrent,
        bool isAccepted,
        Guid createdByUserId,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await EnsureOpenAsync(connection, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = """
            INSERT INTO "AssetValuations"
                ("Id", "FullWorthSpaceId", "AssetId", "Amount", "Currency", "ValuedAt", "Method",
                 "LowEstimate", "HighEstimate", "Confidence", "ProviderKey", "ProviderDisplayName",
                 "ExternalReference", "InputSummaryJson", "IsCurrent", "IsAccepted", "CreatedByUserId", "CreatedAt")
            VALUES
                (@id, @space, @asset, @amount, @currency, @valuedAt, @method,
                 @low, @high, @confidence, @providerKey, @providerName,
                 @externalReference, NULL, @isCurrent, @isAccepted, @createdBy, @createdAt);
            """;
        AddParameter(command, "@id", id);
        AddParameter(command, "@space", fullWorthSpaceId);
        AddParameter(command, "@asset", assetId);
        AddParameter(command, "@amount", value.Amount);
        AddParameter(command, "@currency", value.Currency);
        AddParameter(command, "@valuedAt", value.ValuedAt);
        AddParameter(command, "@method", value.Method);
        AddParameter(command, "@low", value.LowEstimate);
        AddParameter(command, "@high", value.HighEstimate);
        AddParameter(command, "@confidence", value.Confidence);
        AddParameter(command, "@providerKey", value.ProviderKey);
        AddParameter(command, "@providerName", value.ProviderDisplayName);
        AddParameter(command, "@externalReference", value.ExternalReference);
        AddParameter(command, "@isCurrent", isCurrent);
        AddParameter(command, "@isAccepted", isAccepted);
        AddParameter(command, "@createdBy", createdByUserId);
        AddParameter(command, "@createdAt", createdAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<AssetValuationView>> ReadHistoryAsync(Guid fullWorthSpaceId, Guid assetId, CancellationToken ct)
    {
        var rows = new List<AssetValuationView>();
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        await EnsureOpenAsync(connection, ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = SelectSql + " WHERE \"FullWorthSpaceId\"=@space AND \"AssetId\"=@asset ORDER BY \"ValuedAt\" DESC, \"CreatedAt\" DESC, \"Id\" DESC;";
            AddParameter(command, "@space", fullWorthSpaceId);
            AddParameter(command, "@asset", assetId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) rows.Add(ReadView(reader));
            return rows;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    private async Task<AssetValuationView?> ReadOneAsync(Guid fullWorthSpaceId, Guid assetId, Guid valuationId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        await EnsureOpenAsync(connection, ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = SelectSql + " WHERE \"FullWorthSpaceId\"=@space AND \"AssetId\"=@asset AND \"Id\"=@id;";
            AddParameter(command, "@space", fullWorthSpaceId);
            AddParameter(command, "@asset", assetId);
            AddParameter(command, "@id", valuationId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadView(reader) : null;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    private const string SelectSql = """
        SELECT "Id", "FullWorthSpaceId", "AssetId", "Amount", "Currency", "ValuedAt", "Method",
               "LowEstimate", "HighEstimate", "Confidence", "ProviderKey", "ProviderDisplayName",
               "ExternalReference", "IsCurrent", "IsAccepted", "CreatedByUserId", "CreatedAt"
        FROM "AssetValuations"
        """;

    private static AssetValuationView ReadView(DbDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetDecimal(3),
        reader.GetString(4),
        reader.GetFieldValue<DateOnly>(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetDecimal(7),
        reader.IsDBNull(8) ? null : reader.GetDecimal(8),
        reader.IsDBNull(9) ? null : reader.GetDecimal(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetBoolean(13),
        reader.GetBoolean(14),
        reader.IsDBNull(15) ? null : reader.GetGuid(15),
        reader.GetFieldValue<DateTimeOffset>(16));

    private static (NormalizedValuation Value, string? Error) NormalizeAndValidate(AssetValuationWrite request)
    {
        if (request.Amount < 0) return (default, "Valuation amount must be zero or greater.");

        var currency = request.Currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
            return (default, "Currency must be a three-letter code.");

        var method = string.IsNullOrWhiteSpace(request.Method)
            ? "manual"
            : request.Method.Trim().ToLowerInvariant();
        if (!AllowedMethods.Contains(method))
            return (default, "Unsupported valuation method.");

        if (request.LowEstimate is < 0 || request.HighEstimate is < 0)
            return (default, "Estimate bounds must be zero or greater.");
        if (request.LowEstimate.HasValue && request.LowEstimate.Value > request.Amount)
            return (default, "Low estimate cannot exceed the valuation amount.");
        if (request.HighEstimate.HasValue && request.HighEstimate.Value < request.Amount)
            return (default, "High estimate cannot be below the valuation amount.");
        if (request.LowEstimate.HasValue && request.HighEstimate.HasValue && request.LowEstimate > request.HighEstimate)
            return (default, "Low estimate cannot exceed high estimate.");
        if (request.Confidence is < 0 or > 1)
            return (default, "Confidence must be between zero and one.");

        var providerKey = TrimToNull(request.ProviderKey);
        var providerName = TrimToNull(request.ProviderDisplayName);
        var externalReference = TrimToNull(request.ExternalReference);
        if (providerKey?.Length > 100) return (default, "Provider key is too long.");
        if (providerName?.Length > 200) return (default, "Provider name is too long.");
        if (externalReference?.Length > 500) return (default, "External reference is too long.");

        var valuedAt = request.ValuedAt ?? DateOnly.FromDateTime(DateTime.UtcNow);
        return (new NormalizedValuation(
            request.Amount,
            currency,
            valuedAt,
            method,
            request.LowEstimate,
            request.HighEstimate,
            request.Confidence,
            providerKey,
            providerName,
            externalReference), null);
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task EnsureOpenAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private readonly record struct NormalizedValuation(
        decimal Amount,
        string Currency,
        DateOnly ValuedAt,
        string Method,
        decimal? LowEstimate,
        decimal? HighEstimate,
        decimal? Confidence,
        string? ProviderKey,
        string? ProviderDisplayName,
        string? ExternalReference);
}

public static class AssetValuationEndpoints
{
    public static IEndpointRouteBuilder MapAssetValuationEndpoints(this IEndpointRouteBuilder app)
    {
        var valuations = app.MapGroup("/api/assets/{assetId:guid}/valuations").WithTags("Asset valuations");

        valuations.MapGet("/", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            AssetValuationStore store,
            CancellationToken ct) =>
        {
            var rows = await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, assetId, ct);
            return rows is null ? Results.NotFound() : Results.Ok(rows);
        });

        valuations.MapPost("/", async (
            Guid assetId,
            Guid fullWorthSpaceId,
            AssetValuationWrite request,
            CurrentUserContext currentUser,
            AssetValuationStore store,
            CancellationToken ct) =>
            ToResult(await store.CreateForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, assetId, request, ct)));

        return app;
    }

    private static IResult ToResult(AssetValuationMutationOutcome outcome) => outcome.Result switch
    {
        AssetValuationMutationResult.Success => Results.Ok(outcome.Valuation),
        AssetValuationMutationResult.NotFound => Results.NotFound(),
        AssetValuationMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        AssetValuationMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid valuation." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
