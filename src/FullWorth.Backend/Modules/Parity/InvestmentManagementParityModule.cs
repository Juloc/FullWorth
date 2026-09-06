using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record InvestmentPortfolioCreateWrite(
    string Name, string Currency, Guid? AccountId, Guid? BenchmarkSecurityId,
    string? ProviderName, bool IsManual = true, bool IncludeInNetWorth = true);
public sealed record InvestmentSecurityManageWrite(
    string Name, string? Isin, string? Wkn, string? Ticker, string AssetType,
    string Currency, string? Exchange, string? ProviderKey, bool IsActive = true);
public sealed record InvestmentPriceManageWrite(
    Guid SecurityId, DateOnly PriceDate, decimal Price, string Currency, string Source = "manual");
public sealed record InvestmentWatchlistManageWrite(string Name);
public sealed record InvestmentWatchlistItemManageWrite(Guid SecurityId, decimal? TargetPrice, string? Notes, int SortOrder = 0);

public static class InvestmentManagementParityEndpoints
{
    private static readonly HashSet<string> AssetTypes = new(StringComparer.OrdinalIgnoreCase)
    { "stock", "etf", "fund", "bond", "crypto", "commodity", "derivative", "cash", "other" };

    private static readonly HashSet<string> TradeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "buy", "sell", "cancellation", "dividend", "interest", "fee", "tax", "deposit", "withdrawal",
        "security_transfer_in", "security_transfer_out", "split", "other"
    };

    public static IEndpointRouteBuilder MapInvestmentManagementParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/investment-management").WithTags("Investments");
        group.MapPost("/portfolios", CreatePortfolio);
        group.MapPost("/securities", CreateSecurity);
        group.MapPut("/securities/{securityId:guid}", UpdateSecurity);
        group.MapPut("/prices", PutPrice);
        group.MapPut("/portfolios/{portfolioId:guid}/trades/{tradeId:guid}", UpdateTrade);
        group.MapDelete("/portfolios/{portfolioId:guid}/trades/{tradeId:guid}", DeleteTrade);
        group.MapGet("/watchlists", ListWatchlists);
        group.MapPost("/watchlists", CreateWatchlist);
        group.MapPut("/watchlists/{watchlistId:guid}", UpdateWatchlist);
        group.MapDelete("/watchlists/{watchlistId:guid}", DeleteWatchlist);
        group.MapGet("/watchlists/{watchlistId:guid}/items", GetWatchlistItems);
        group.MapPut("/watchlists/{watchlistId:guid}/items", PutWatchlistItems);
        return app;
    }

    private static async Task<IResult> CreatePortfolio(
        Guid fullWorthSpaceId, InvestmentPortfolioCreateWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name) || !ValidCurrency(request.Currency))
            return Results.BadRequest(new { error = "Name and valid currency are required." });
        if (request.AccountId.HasValue)
        {
            var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
            if (!visible.Contains(request.AccountId.Value)) return Results.BadRequest(new { error = "Linked account is inaccessible." });
        }
        if (request.BenchmarkSecurityId.HasValue && !await SecurityExists(db, fullWorthSpaceId, request.BenchmarkSecurityId.Value, ct))
            return Results.BadRequest(new { error = "Benchmark security is invalid." });

        var id = Guid.NewGuid();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId","ProviderName","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@currency,@account,@benchmark,@provider,@manual,@include,false,@now,@now)
""", ("@id", id), ("@space", fullWorthSpaceId), ("@name", request.Name.Trim()),
            ("@currency", request.Currency.Trim().ToUpperInvariant()), ("@account", request.AccountId),
            ("@benchmark", request.BenchmarkSecurityId), ("@provider", Clean(request.ProviderName)),
            ("@manual", request.IsManual), ("@include", request.IncludeInNetWorth), ("@now", DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId, userId, "investment.portfolio.created", "InvestmentPortfolio", id);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/investments/portfolios/{id}", new { id });
    }

    private static async Task<IResult> CreateSecurity(
        Guid fullWorthSpaceId, InvestmentSecurityManageWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
        await WriteSecurity(Guid.NewGuid(), fullWorthSpaceId, request, currentUser, db, audit, false, ct);

    private static async Task<IResult> UpdateSecurity(
        Guid securityId, Guid fullWorthSpaceId, InvestmentSecurityManageWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
        await WriteSecurity(securityId, fullWorthSpaceId, request, currentUser, db, audit, true, ct);

    private static async Task<IResult> WriteSecurity(
        Guid id, Guid fullWorthSpaceId, InvestmentSecurityManageWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, bool update, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name) || !ValidCurrency(request.Currency))
            return Results.BadRequest(new { error = "Name and valid currency are required." });
        var assetType = request.AssetType.Trim().ToLowerInvariant();
        if (!AssetTypes.Contains(assetType)) return Results.BadRequest(new { error = "Unsupported asset type." });
        var isin = Clean(request.Isin)?.ToUpperInvariant();
        if (isin is { Length: > 0 } && isin.Length != 12) return Results.BadRequest(new { error = "ISIN must contain 12 characters." });

        var connection = await ParitySql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        await using var command = update
            ? ParitySql.Command(connection, """
UPDATE "Securities" SET "Name"=@name,"Isin"=@isin,"Wkn"=@wkn,"Ticker"=@ticker,"AssetType"=@type,
 "Currency"=@currency,"Exchange"=@exchange,"ProviderKey"=@provider,"IsActive"=@active,"UpdatedAt"=@now
WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""", ("@name", request.Name.Trim()), ("@isin", isin), ("@wkn", Clean(request.Wkn)?.ToUpperInvariant()),
                ("@ticker", Clean(request.Ticker)?.ToUpperInvariant()), ("@type", assetType),
                ("@currency", request.Currency.Trim().ToUpperInvariant()), ("@exchange", Clean(request.Exchange)),
                ("@provider", Clean(request.ProviderKey)), ("@active", request.IsActive), ("@now", now),
                ("@id", id), ("@space", fullWorthSpaceId))
            : ParitySql.Command(connection, """
INSERT INTO "Securities"
("Id","FullWorthSpaceId","Name","Isin","Wkn","Ticker","AssetType","Currency","Exchange","ProviderKey","IsActive","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@isin,@wkn,@ticker,@type,@currency,@exchange,@provider,@active,@now,@now)
""", ("@id", id), ("@space", fullWorthSpaceId), ("@name", request.Name.Trim()), ("@isin", isin),
                ("@wkn", Clean(request.Wkn)?.ToUpperInvariant()), ("@ticker", Clean(request.Ticker)?.ToUpperInvariant()),
                ("@type", assetType), ("@currency", request.Currency.Trim().ToUpperInvariant()),
                ("@exchange", Clean(request.Exchange)), ("@provider", Clean(request.ProviderKey)),
                ("@active", request.IsActive), ("@now", now));
        try
        {
            if (await command.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        }
        catch (Exception exception) when (exception.Message.Contains("IX_Securities_Space_Isin", StringComparison.OrdinalIgnoreCase) ||
                                          exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = "A security with this ISIN already exists." });
        }
        audit.Record(fullWorthSpaceId, userId, update ? "investment.security.updated" : "investment.security.created", "Security", id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id });
    }

    private static async Task<IResult> PutPrice(
        Guid fullWorthSpaceId, InvestmentPriceManageWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.Price <= 0 || !ValidCurrency(request.Currency) || !await SecurityExists(db, fullWorthSpaceId, request.SecurityId, ct))
            return Results.BadRequest(new { error = "Security, positive price and valid currency are required." });
        var source = string.IsNullOrWhiteSpace(request.Source) ? "manual" : request.Source.Trim().ToLowerInvariant();
        if (source.Length > 64) return Results.BadRequest(new { error = "Price source is too long." });
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt")
VALUES (@security,@date,@price,@currency,@source,@now)
ON CONFLICT ("SecurityId","PriceDate","Source") DO UPDATE SET "Price"=EXCLUDED."Price","Currency"=EXCLUDED."Currency"
""", ("@security", request.SecurityId), ("@date", request.PriceDate), ("@price", request.Price),
            ("@currency", request.Currency.Trim().ToUpperInvariant()), ("@source", source), ("@now", DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId, userId, "investment.price.updated", "Security", request.SecurityId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateTrade(
        Guid portfolioId, Guid tradeId, Guid fullWorthSpaceId, InvestmentTradeV2Write request,
        CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await PortfolioExists(db, fullWorthSpaceId, portfolioId, ct)) return Results.NotFound();
        var type = request.TradeType.Trim().ToLowerInvariant();
        var error = await ValidateTrade(db, fullWorthSpaceId, request, type, ct);
        if (error is not null) return Results.BadRequest(new { error });

        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
UPDATE "InvestmentTrades" SET "SecurityId"=@security,"TradeType"=@type,"TradeDate"=@date,
 "SettlementDate"=@settlement,"Quantity"=@quantity,"Price"=@price,"GrossAmount"=@gross,"Amount"=@amount,
 "Currency"=@currency,"Fees"=@fees,"Taxes"=@taxes,"WithholdingTax"=@withholding,"Source"=@source,
 "ExternalKey"=@external,"Notes"=@notes,"UpdatedAt"=@now
WHERE "Id"=@id AND "PortfolioId"=@portfolio AND "FullWorthSpaceId"=@space
""", ("@security", request.SecurityId), ("@type", type), ("@date", request.TradeDate),
            ("@settlement", request.SettlementDate), ("@quantity", request.Quantity), ("@price", request.Price),
            ("@gross", request.GrossAmount), ("@amount", request.Amount),
            ("@currency", request.Currency.Trim().ToUpperInvariant()), ("@fees", request.Fees),
            ("@taxes", request.Taxes), ("@withholding", request.WithholdingTax),
            ("@source", NormalizeSource(request.Source)), ("@external", Clean(request.ExternalKey)),
            ("@notes", Clean(request.Notes)), ("@now", DateTimeOffset.UtcNow), ("@id", tradeId),
            ("@portfolio", portfolioId), ("@space", fullWorthSpaceId));
        try
        {
            if (await command.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        }
        catch (Exception exception) when (exception.Message.Contains("Cannot sell", StringComparison.OrdinalIgnoreCase) ||
                                          exception.Message.Contains("Cannot dispose", StringComparison.OrdinalIgnoreCase) ||
                                          exception.Message.Contains("oversold", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = exception.Message });
        }
        audit.Record(fullWorthSpaceId, userId, "investment.trade.updated", "InvestmentTrade", tradeId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteTrade(
        Guid portfolioId, Guid tradeId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "DELETE FROM \"InvestmentTrades\" WHERE \"Id\"=@id AND \"PortfolioId\"=@portfolio AND \"FullWorthSpaceId\"=@space",
            ("@id", tradeId), ("@portfolio", portfolioId), ("@space", fullWorthSpaceId));
        if (await command.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        audit.Record(fullWorthSpaceId, userId, "investment.trade.deleted", "InvestmentTrade", tradeId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListWatchlists(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Id","Name","CreatedAt","UpdatedAt" FROM "Watchlists"
WHERE "FullWorthSpaceId"=@space AND "OwnerUserId"=@user ORDER BY "Name"
""", ("@space", fullWorthSpaceId), ("@user", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct)) rows.Add(new
        {
            id = ParitySql.Guid(reader, "Id"), name = ParitySql.String(reader, "Name"),
            createdAt = ParitySql.Timestamp(reader, "CreatedAt"), updatedAt = ParitySql.Timestamp(reader, "UpdatedAt")
        });
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateWatchlist(
        Guid fullWorthSpaceId, InvestmentWatchlistManageWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Name is required." });
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
INSERT INTO "Watchlists" ("Id","FullWorthSpaceId","OwnerUserId","Name","CreatedAt","UpdatedAt")
VALUES (@id,@space,@user,@name,@now,@now)
""", ("@id", id), ("@space", fullWorthSpaceId), ("@user", userId), ("@name", request.Name.Trim()), ("@now", now));
        await command.ExecuteNonQueryAsync(ct);
        audit.Record(fullWorthSpaceId, userId, "investment.watchlist.created", "Watchlist", id);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/investment-management/watchlists/{id}", new { id });
    }

    private static async Task<IResult> UpdateWatchlist(
        Guid watchlistId, Guid fullWorthSpaceId, InvestmentWatchlistManageWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Name is required." });
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
UPDATE "Watchlists" SET "Name"=@name,"UpdatedAt"=@now
WHERE "Id"=@id AND "FullWorthSpaceId"=@space AND "OwnerUserId"=@user
""", ("@name", request.Name.Trim()), ("@now", DateTimeOffset.UtcNow), ("@id", watchlistId),
            ("@space", fullWorthSpaceId), ("@user", userId));
        if (await command.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        audit.Record(fullWorthSpaceId, userId, "investment.watchlist.updated", "Watchlist", watchlistId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteWatchlist(
        Guid watchlistId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "DELETE FROM \"Watchlists\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"OwnerUserId\"=@user",
            ("@id", watchlistId), ("@space", fullWorthSpaceId), ("@user", userId));
        if (await command.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        audit.Record(fullWorthSpaceId, userId, "investment.watchlist.deleted", "Watchlist", watchlistId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetWatchlistItems(
        Guid watchlistId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await OwnWatchlist(db, watchlistId, fullWorthSpaceId, userId, ct)) return Results.NotFound();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT i."SecurityId",s."Name",s."Ticker",i."TargetPrice",i."Notes",i."SortOrder"
FROM "WatchlistItems" i JOIN "Securities" s ON s."Id"=i."SecurityId"
WHERE i."WatchlistId"=@id ORDER BY i."SortOrder",s."Name"
""", ("@id", watchlistId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct)) rows.Add(new
        {
            securityId = ParitySql.Guid(reader, "SecurityId"), name = ParitySql.String(reader, "Name"),
            ticker = ParitySql.NullableString(reader, "Ticker"), targetPrice = ParitySql.NullableDecimal(reader, "TargetPrice"),
            notes = ParitySql.NullableString(reader, "Notes"), sortOrder = ParitySql.Int(reader, "SortOrder")
        });
        return Results.Ok(rows);
    }

    private static async Task<IResult> PutWatchlistItems(
        Guid watchlistId, Guid fullWorthSpaceId, IReadOnlyList<InvestmentWatchlistItemManageWrite> request,
        CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await CanManage(db, userId, fullWorthSpaceId, ct)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await OwnWatchlist(db, watchlistId, fullWorthSpaceId, userId, ct)) return Results.NotFound();
        var items = request.DistinctBy(item => item.SecurityId).ToArray();
        if (items.Length > 500) return Results.BadRequest(new { error = "Watchlist is too large." });
        foreach (var item in items)
        {
            if (item.TargetPrice is <= 0) return Results.BadRequest(new { error = "Target price must be positive." });
            if (!await SecurityExists(db, fullWorthSpaceId, item.SecurityId, ct)) return Results.BadRequest(new { error = "Security is invalid." });
        }
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using (var delete = ParitySql.Command(connection, "DELETE FROM \"WatchlistItems\" WHERE \"WatchlistId\"=@id", ("@id", watchlistId)))
            await delete.ExecuteNonQueryAsync(ct);
        foreach (var item in items)
        {
            await using var command = ParitySql.Command(connection, """
INSERT INTO "WatchlistItems" ("WatchlistId","SecurityId","TargetPrice","Notes","SortOrder")
VALUES (@watchlist,@security,@target,@notes,@sort)
""", ("@watchlist", watchlistId), ("@security", item.SecurityId), ("@target", item.TargetPrice),
                ("@notes", Clean(item.Notes)), ("@sort", item.SortOrder));
            await command.ExecuteNonQueryAsync(ct);
        }
        audit.Record(fullWorthSpaceId, userId, "investment.watchlist.items.updated", "Watchlist", watchlistId);
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<string?> ValidateTrade(
        FullWorthDbContext db, Guid fullWorthSpaceId, InvestmentTradeV2Write request, string type, CancellationToken ct)
    {
        if (!TradeTypes.Contains(type)) return "Unsupported investment transaction type.";
        if (!ValidCurrency(request.Currency) || request.Amount < 0 || request.Fees < 0 || request.Taxes < 0 || request.WithholdingTax < 0)
            return "Amounts and currency are invalid.";
        if (request.SecurityId.HasValue && !await SecurityExists(db, fullWorthSpaceId, request.SecurityId.Value, ct))
            return "Security is invalid.";
        if (type is "buy" or "sell" or "cancellation" or "security_transfer_in" or "security_transfer_out" &&
            (!request.SecurityId.HasValue || request.Quantity is null or <= 0))
            return "This transaction requires a security and positive quantity.";
        if (type is "buy" or "sell" && request.Price is null or <= 0 && request.GrossAmount is null or <= 0)
            return "Buy/sell requires a positive price or gross amount.";
        if (type == "split" && (!request.SecurityId.HasValue || request.Quantity is null or <= 0))
            return "Split quantity stores the positive split ratio, e.g. 2 for 2:1.";
        return null;
    }

    private static async Task<bool> CanManage(FullWorthDbContext db, Guid userId, Guid space, CancellationToken ct) =>
        await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, space, "investments.manage", ct);

    private static async Task<bool> SecurityExists(FullWorthDbContext db, Guid space, Guid id, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"Securities\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space)",
            ("@id", id), ("@space", space));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> PortfolioExists(FullWorthDbContext db, Guid space, Guid id, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"InvestmentPortfolios\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space)",
            ("@id", id), ("@space", space));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> OwnWatchlist(FullWorthDbContext db, Guid id, Guid space, Guid userId, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT EXISTS(SELECT 1 FROM "Watchlists" WHERE "Id"=@id AND "FullWorthSpaceId"=@space AND "OwnerUserId"=@user)
""", ("@id", id), ("@space", space), ("@user", userId));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(char.IsLetter);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeSource(string? source) => string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim().ToLowerInvariant() switch
    { "manual" => "manual", "import" => "import", "provider" => "provider", _ => "manual" };
}
