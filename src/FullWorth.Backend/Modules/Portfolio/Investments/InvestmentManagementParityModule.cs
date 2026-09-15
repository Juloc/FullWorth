using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

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
        SpaceAccess space, InvestmentStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name) || !ValidCurrency(request.Currency))
            return Results.BadRequest(new { error = "Name and valid currency are required." });

        if (request.AccountId.HasValue)
        {
            var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
            if (!visible.Contains(request.AccountId.Value))
                return Results.BadRequest(new { error = "Linked account is inaccessible." });
        }
        if (request.BenchmarkSecurityId.HasValue
            && !await store.SecurityExistsAsync(fullWorthSpaceId, request.BenchmarkSecurityId.Value, ct))
            return Results.BadRequest(new { error = "Benchmark security is invalid." });

        var id = await store.CreatePortfolioAsync(userId, fullWorthSpaceId, request, Clean(request.ProviderName), ct);
        return Results.Created($"/api/investments/portfolios/{id}", new { id });
    }

    private static Task<IResult> CreateSecurity(
        Guid fullWorthSpaceId, InvestmentSecurityManageWrite request, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct) =>
        WriteSecurity(Guid.NewGuid(), fullWorthSpaceId, request, currentUser, store, false, ct);

    private static Task<IResult> UpdateSecurity(
        Guid securityId, Guid fullWorthSpaceId, InvestmentSecurityManageWrite request, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct) =>
        WriteSecurity(securityId, fullWorthSpaceId, request, currentUser, store, true, ct);

    private static async Task<IResult> WriteSecurity(
        Guid id, Guid fullWorthSpaceId, InvestmentSecurityManageWrite request, CurrentUserContext currentUser,
        InvestmentStore store, bool update, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name) || !ValidCurrency(request.Currency))
            return Results.BadRequest(new { error = "Name and valid currency are required." });

        var assetType = request.AssetType.Trim().ToLowerInvariant();
        if (!AssetTypes.Contains(assetType)) return Results.BadRequest(new { error = "Unsupported asset type." });

        var isin = Clean(request.Isin)?.ToUpperInvariant();
        if (isin is { Length: > 0 } && isin.Length != 12)
            return Results.BadRequest(new { error = "ISIN must contain 12 characters." });

        try
        {
            return await store.SaveSecurityAsync(userId, fullWorthSpaceId, id, request, assetType, isin,
                Clean(request.Wkn)?.ToUpperInvariant(), Clean(request.Ticker)?.ToUpperInvariant(),
                Clean(request.Exchange), Clean(request.ProviderKey), update, ct)
                ? Results.Ok(new { id })
                : Results.NotFound();
        }
        catch (Exception exception) when (
            exception.Message.Contains("IX_Securities_Space_Isin", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = "A security with this ISIN already exists." });
        }
    }

    private static async Task<IResult> PutPrice(
        Guid fullWorthSpaceId, InvestmentPriceManageWrite request, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request.Price <= 0 || !ValidCurrency(request.Currency)
            || !await store.SecurityExistsAsync(fullWorthSpaceId, request.SecurityId, ct))
            return Results.BadRequest(new { error = "Security, positive price and valid currency are required." });

        var source = string.IsNullOrWhiteSpace(request.Source) ? "manual" : request.Source.Trim().ToLowerInvariant();
        if (source.Length > 64) return Results.BadRequest(new { error = "Price source is too long." });

        await store.SavePriceAsync(userId, fullWorthSpaceId, request.SecurityId, request.PriceDate, request.Price,
            request.Currency.Trim().ToUpperInvariant(), source, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateTrade(
        Guid portfolioId, Guid tradeId, Guid fullWorthSpaceId, InvestmentTradeV2Write request,
        CurrentUserContext currentUser, InvestmentStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await store.PortfolioExistsAsync(fullWorthSpaceId, portfolioId, ct)) return Results.NotFound();

        var type = request.TradeType.Trim().ToLowerInvariant();
        var error = await ValidateTrade(store, fullWorthSpaceId, request, type, ct);
        if (error is not null) return Results.BadRequest(new { error });

        try
        {
            // Die Datenbank laesst keinen Bestand unter null zu. Der Versuch ist ein Konflikt, kein
            // Serverfehler - und ihre Meldung sagt genauer, welcher Handel im Weg steht.
            return await store.UpdateTradeAsync(userId, fullWorthSpaceId, portfolioId, tradeId, request, type,
                NormalizeSource(request.Source), Clean(request.ExternalKey), Clean(request.Notes), ct)
                ? Results.NoContent()
                : Results.NotFound();
        }
        catch (Exception exception) when (
            exception.Message.Contains("Cannot sell", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("Cannot dispose", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("oversold", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> DeleteTrade(
        Guid portfolioId, Guid tradeId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await store.DeleteTradeAsync(userId, fullWorthSpaceId, portfolioId, tradeId, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> ListWatchlists(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, InvestmentStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var rows = (await store.ListWatchlistsAsync(fullWorthSpaceId, userId, ct))
            .Select(row => new
            {
                id = row.Id,
                name = row.Name,
                createdAt = row.CreatedAt,
                updatedAt = row.UpdatedAt
            });
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateWatchlist(
        Guid fullWorthSpaceId, InvestmentWatchlistManageWrite request, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Name is required." });

        var id = Guid.NewGuid();
        await store.SaveWatchlistAsync(userId, fullWorthSpaceId, id, request.Name, false,
            "investment.watchlist.created", ct);
        return Results.Created($"/api/investment-management/watchlists/{id}", new { id });
    }

    private static async Task<IResult> UpdateWatchlist(
        Guid watchlistId, Guid fullWorthSpaceId, InvestmentWatchlistManageWrite request,
        CurrentUserContext currentUser, InvestmentStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Name is required." });

        return await store.SaveWatchlistAsync(userId, fullWorthSpaceId, watchlistId, request.Name, true,
            "investment.watchlist.updated", ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> DeleteWatchlist(
        Guid watchlistId, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await store.DeleteWatchlistAsync(userId, fullWorthSpaceId, watchlistId,
            "investment.watchlist.deleted", ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> GetWatchlistItems(
        Guid watchlistId, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.OwnsWatchlistAsync(userId, fullWorthSpaceId, watchlistId, ct)) return Results.NotFound();

        var rows = (await store.ListWatchlistItemsAsync(watchlistId, ct))
            .Select(row => new
            {
                securityId = row.SecurityId,
                name = row.Name,
                ticker = row.Ticker,
                targetPrice = row.TargetPrice,
                notes = row.Notes,
                sortOrder = row.SortOrder
            });
        return Results.Ok(rows);
    }

    private static async Task<IResult> PutWatchlistItems(
        Guid watchlistId, Guid fullWorthSpaceId, IReadOnlyList<InvestmentWatchlistItemManageWrite> request,
        CurrentUserContext currentUser, InvestmentStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await store.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await store.OwnsWatchlistAsync(userId, fullWorthSpaceId, watchlistId, ct)) return Results.NotFound();

        var items = request.DistinctBy(item => item.SecurityId).ToArray();
        if (items.Length > 500) return Results.BadRequest(new { error = "Watchlist is too large." });
        if (items.Any(item => item.TargetPrice is <= 0))
            return Results.BadRequest(new { error = "Target price must be positive." });
        if (!await store.AllSecuritiesExistAsync(fullWorthSpaceId, items.Select(item => item.SecurityId).ToArray(), ct))
            return Results.BadRequest(new { error = "Security is invalid." });

        await store.ReplaceWatchlistItemsAsync(userId, fullWorthSpaceId, watchlistId,
            items.Select(item => (item.SecurityId, item.TargetPrice, Clean(item.Notes), item.SortOrder)).ToArray(),
            "investment.watchlist.items.updated", ct);
        return Results.NoContent();
    }

    private static async Task<string?> ValidateTrade(
        InvestmentStore store, Guid fullWorthSpaceId, InvestmentTradeV2Write request, string type, CancellationToken ct)
    {
        if (!TradeTypes.Contains(type)) return "Unsupported investment transaction type.";
        if (!ValidCurrency(request.Currency) || request.Amount < 0 || request.Fees < 0 || request.Taxes < 0 || request.WithholdingTax < 0)
            return "Amounts and currency are invalid.";
        if (request.SecurityId.HasValue && !await store.SecurityExistsAsync(fullWorthSpaceId, request.SecurityId.Value, ct))
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

    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(char.IsLetter);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string NormalizeSource(string? source) => string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim().ToLowerInvariant() switch
    { "manual" => "manual", "import" => "import", "provider" => "provider", _ => "manual" };
}
