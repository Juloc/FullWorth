using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed record PortfolioWrite(string Name,string Currency,Guid? AccountId,Guid? BenchmarkSecurityId,bool IsArchived=false);
public sealed record SecurityWrite(string Name,string? Isin,string? Wkn,string? Ticker,string AssetType,string Currency,string? Exchange);
public sealed record TradeWrite(Guid? SecurityId,string TradeType,DateOnly TradeDate,decimal? Quantity,decimal? Price,decimal Amount,string Currency,decimal Fees=0,decimal Taxes=0,string? ExternalKey=null,string? Notes=null);
public sealed record WatchlistWrite(string Name);
public sealed record WatchlistItemWrite(Guid SecurityId,decimal? TargetPrice,string? Notes);

public static class InvestmentEndpoints
{
    public static IEndpointRouteBuilder MapInvestmentEndpoints(this IEndpointRouteBuilder app)
    {
        var p=app.MapGroup("/api/investments").WithTags("Investments");
        p.MapGet("/portfolios",ListPortfolios);p.MapPost("/portfolios",CreatePortfolio);p.MapPut("/portfolios/{id:guid}",UpdatePortfolio);p.MapDelete("/portfolios/{id:guid}",ArchivePortfolio);
        p.MapGet("/securities",ListSecurities);p.MapPost("/securities",CreateSecurity);p.MapPut("/securities/{id:guid}",UpdateSecurity);
        p.MapGet("/portfolios/{portfolioId:guid}/trades",ListTrades);
        p.MapGet("/portfolios/{portfolioId:guid}/dividends",Dividends);
        p.MapGet("/watchlists",ListWatchlists);p.MapPost("/watchlists",CreateWatchlist);p.MapPut("/watchlists/{id:guid}",UpdateWatchlist);p.MapDelete("/watchlists/{id:guid}",DeleteWatchlist);p.MapPut("/watchlists/{id:guid}/items",PutWatchlistItems);p.MapGet("/watchlists/{id:guid}/items",GetWatchlistItems);
        return app;
    }

    /// <summary>
    /// Die Depots - mit ihrem Wert und ihrem Gewinn.
    ///
    /// Die Liste nannte bisher nur Namen und Waehrung. Beide Seiten, die Depots zeigen - die
    /// Kontenliste und die Vermoegensseite -, holen genau sie; jede haette sonst je Depot einen
    /// eigenen Aufruf gebraucht, um eine Prozentzahl anzuzeigen.
    ///
    /// Die Bewertung laeuft je Depot. Das ist eine Handvoll, keine Liste ohne Ende: ein Depot
    /// entsteht pro Bankdepot oder von Hand.
    ///
    /// <c>costBasis</c> und <c>unrealizedResult</c> sind NULL, wenn kein Einstand bekannt ist, und
    /// nicht 0. Die Prozentzahl bildet die Anzeige daraus - nur sie weiss, ob sie eine zeigen will.
    ///
    /// Ein Depot, das an einem Konto haengt, das dieser Benutzer nicht sehen darf, faellt heraus. Das
    /// stand bis 2026-09-23 in einer Middleware, die die Route vor der Zuordnung abfing - und die
    /// beantwortete sie mit NUR den Stammdaten. Die Werte oben kamen also nie an: die Vermoegensseite
    /// liest totalValue, costBasis, unrealizedResult und gainIncomplete seit jeher und bekam nichts.
    /// </summary>
    private static async Task<IResult> ListPortfolios(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, InvestmentStore store,
        PortfolioValuationStore valuationStore, PortfolioValuationService valuation, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(uid, fullWorthSpaceId, ct)) return Results.NotFound();

        var visibleAccounts = await space.VisibleAccountIdsAsync(uid, fullWorthSpaceId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var rows = new List<object>();
        foreach (var row in await store.ListPortfoliosAsync(fullWorthSpaceId, ct))
        {
            if (row.AccountId.HasValue && !visibleAccounts.Contains(row.AccountId.Value)) continue;

            var settings = await valuationStore.FindPortfolioAsync(fullWorthSpaceId, row.Id, ct);
            var calculation = settings is null ? null : await valuation.CalculateAsync(settings, today, ct);
            var gain = calculation is null
                ? new PortfolioGainView(null, null, false)
                : PortfolioValuationService.Gain(calculation.Positions);
            rows.Add(new
            {
                id = row.Id,
                name = row.Name,
                currency = row.Currency,
                accountId = row.AccountId,
                benchmarkSecurityId = row.BenchmarkSecurityId,
                isArchived = row.IsArchived,
                createdAt = row.CreatedAt,
                updatedAt = row.UpdatedAt,
                totalValue = calculation?.TotalValue,
                marketValue = calculation?.SecurityValue,
                positions = calculation?.Positions.Count ?? 0,
                costBasis = gain.CostBasis,
                unrealizedResult = gain.UnrealizedResult,
                gainIncomplete = gain.Incomplete,
                incomplete = calculation?.Incomplete ?? false
            });
        }
        return Results.Ok(rows);
    }

    private static Task<IResult> CreatePortfolio(
        Guid fullWorthSpaceId, PortfolioWrite request, CurrentUserContext currentUser, SpaceAccess space,
        InvestmentStore store, CancellationToken ct) =>
        WritePortfolio(Guid.NewGuid(), fullWorthSpaceId, request, currentUser, space, store, false, ct);

    private static Task<IResult> UpdatePortfolio(
        Guid id, Guid fullWorthSpaceId, PortfolioWrite request, CurrentUserContext currentUser, SpaceAccess space,
        InvestmentStore store, CancellationToken ct) =>
        WritePortfolio(id, fullWorthSpaceId, request, currentUser, space, store, true, ct);

    private static async Task<IResult> WritePortfolio(
        Guid id, Guid fullWorthSpaceId, PortfolioWrite request, CurrentUserContext currentUser, SpaceAccess space,
        InvestmentStore store, bool update, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.CanManageAsync(uid, fullWorthSpaceId, ct)) return Results.StatusCode(403);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Currency.Trim().Length != 3) return Results.BadRequest();

        if (request.AccountId.HasValue)
        {
            var visible = await space.VisibleAccountIdsAsync(uid, fullWorthSpaceId, ct);
            if (!visible.Contains(request.AccountId.Value))
                return Results.BadRequest(new { error = "Portfolio account is inaccessible." });
        }
        if (request.BenchmarkSecurityId.HasValue
            && !await store.SecurityExistsAsync(fullWorthSpaceId, request.BenchmarkSecurityId.Value, ct))
            return Results.BadRequest(new { error = "Benchmark security is invalid." });

        return await store.SavePortfolioBasicsAsync(uid, fullWorthSpaceId, id, request, update, ct)
            ? Results.Ok(new { id })
            : Results.NotFound();
    }

    private static async Task<IResult> ArchivePortfolio(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.PortfolioWritableAsync(uid, fullWorthSpaceId, id, ct)) return Results.StatusCode(403);

        return await store.ArchivePortfolioAsync(uid, fullWorthSpaceId, id, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> ListSecurities(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, InvestmentStore store,
        CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(uid, fullWorthSpaceId, ct)) return Results.NotFound();

        var rows = (await store.ListSecuritiesAsync(fullWorthSpaceId, ct))
            .Select(row => new
            {
                id = row.Id,
                name = row.Name,
                isin = row.Isin,
                wkn = row.Wkn,
                ticker = row.Ticker,
                assetType = row.AssetType,
                currency = row.Currency,
                exchange = row.Exchange
            });
        return Results.Ok(rows);
    }

    private static Task<IResult> CreateSecurity(
        Guid fullWorthSpaceId, SecurityWrite request, CurrentUserContext currentUser, InvestmentStore store,
        CancellationToken ct) =>
        WriteSecurity(Guid.NewGuid(), fullWorthSpaceId, request, currentUser, store, false, ct);

    private static Task<IResult> UpdateSecurity(
        Guid id, Guid fullWorthSpaceId, SecurityWrite request, CurrentUserContext currentUser, InvestmentStore store,
        CancellationToken ct) =>
        WriteSecurity(id, fullWorthSpaceId, request, currentUser, store, true, ct);

    private static async Task<IResult> WriteSecurity(
        Guid id, Guid fullWorthSpaceId, SecurityWrite request, CurrentUserContext currentUser, InvestmentStore store,
        bool update, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.CanManageAsync(uid, fullWorthSpaceId, ct)) return Results.StatusCode(403);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Currency.Trim().Length != 3) return Results.BadRequest();

        try
        {
            // Dieselbe ISIN zweimal im selben Bereich laesst die Datenbank nicht zu - das ist ein
            // Konflikt und kein Serverfehler.
            return await store.SaveSecurityBasicsAsync(
                uid, fullWorthSpaceId, id, request, NormalizeAssetType(request.AssetType), update, ct)
                ? Results.Ok(new { id })
                : Results.NotFound();
        }
        catch (Exception)
        {
            return Results.Conflict(new { error = "A security with this ISIN may already exist." });
        }
    }

    private static async Task<IResult> ListTrades(
        Guid portfolioId, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentStore store,
        CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.PortfolioReadableAsync(uid, fullWorthSpaceId, portfolioId, ct)) return Results.NotFound();

        var rows = (await store.ListTradesAsync(portfolioId, ct))
            .Select(row => new
            {
                id = row.Id,
                securityId = row.SecurityId,
                tradeType = row.TradeType,
                tradeDate = row.TradeDate,
                quantity = row.Quantity,
                price = row.Price,
                amount = row.Amount,
                currency = row.Currency,
                fees = row.Fees,
                taxes = row.Taxes,
                externalKey = row.ExternalKey,
                notes = row.Notes
            });
        return Results.Ok(rows);
    }

    private static async Task<IResult> Dividends(
        Guid portfolioId, Guid fullWorthSpaceId, int? year, CurrentUserContext currentUser, InvestmentStore store,
        CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.PortfolioReadableAsync(uid, fullWorthSpaceId, portfolioId, ct)) return Results.NotFound();

        var dividends = await store.ListDividendsAsync(portfolioId, year, ct);
        return Results.Ok(new
        {
            total = dividends.Sum(row => row.Amount),
            items = dividends.Select(row => new
            {
                id = row.Id,
                securityId = row.SecurityId,
                security = row.Security,
                date = row.Date,
                amount = row.Amount,
                currency = row.Currency,
                taxes = row.Taxes
            })
        });
    }

    private static async Task<IResult> ListWatchlists(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, InvestmentStore store,
        CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(uid, fullWorthSpaceId, ct)) return Results.NotFound();

        var rows = (await store.ListWatchlistsAsync(fullWorthSpaceId, uid, ct))
            .Select(row => new { id = row.Id, name = row.Name });
        return Results.Ok(rows);
    }

    private static Task<IResult> CreateWatchlist(
        Guid fullWorthSpaceId, WatchlistWrite request, CurrentUserContext currentUser, SpaceAccess space,
        InvestmentStore store, CancellationToken ct) =>
        WriteWatchlist(Guid.NewGuid(), fullWorthSpaceId, request, currentUser, space, store, false, ct);

    private static Task<IResult> UpdateWatchlist(
        Guid id, Guid fullWorthSpaceId, WatchlistWrite request, CurrentUserContext currentUser, SpaceAccess space,
        InvestmentStore store, CancellationToken ct) =>
        WriteWatchlist(id, fullWorthSpaceId, request, currentUser, space, store, true, ct);

    private static async Task<IResult> WriteWatchlist(
        Guid id, Guid fullWorthSpaceId, WatchlistWrite request, CurrentUserContext currentUser, SpaceAccess space,
        InvestmentStore store, bool update, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(uid, fullWorthSpaceId, ct)) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest();

        return await store.SaveWatchlistAsync(uid, fullWorthSpaceId, id, request.Name, update,
            update ? "watchlist.updated" : "watchlist.created", ct)
            ? Results.Ok(new { id })
            : Results.NotFound();
    }

    private static async Task<IResult> DeleteWatchlist(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentStore store, CancellationToken ct) =>
        await store.DeleteWatchlistAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, "watchlist.deleted", ct)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> PutWatchlistItems(
        Guid id, Guid fullWorthSpaceId, IReadOnlyList<WatchlistItemWrite> request, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.OwnsWatchlistAsync(uid, fullWorthSpaceId, id, ct)) return Results.NotFound();

        var items = request.DistinctBy(item => item.SecurityId).ToArray();
        if (!await store.AllSecuritiesExistAsync(fullWorthSpaceId, items.Select(item => item.SecurityId).ToArray(), ct))
            return Results.BadRequest();

        await store.ReplaceWatchlistItemsAsync(uid, fullWorthSpaceId, id,
            items.Select(item => (item.SecurityId, item.TargetPrice, item.Notes?.Trim(), 0)).ToArray(),
            "watchlist.items.updated", ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetWatchlistItems(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.OwnsWatchlistAsync(uid, fullWorthSpaceId, id, ct)) return Results.NotFound();

        var rows = (await store.ListWatchlistItemsAsync(id, ct))
            .Select(row => new
            {
                securityId = row.SecurityId,
                name = row.Name,
                ticker = row.Ticker,
                assetType = row.AssetType,
                targetPrice = row.TargetPrice,
                notes = row.Notes,
                price = row.Price,
                priceDate = row.PriceDate,
                currency = row.PriceCurrency
            });
        return Results.Ok(rows);
    }

    private static string NormalizeAssetType(string? v)=>v?.Trim().ToLowerInvariant() switch{"etf"=>"etf","stock"=>"stock","fund"=>"fund","bond"=>"bond","crypto"=>"crypto","commodity"=>"commodity","metal"=>"commodity","derivative"=>"derivative","cash"=>"cash",_=>"other"};
    private static string NormalizeTradeType(string? v)=>v?.Trim().ToLowerInvariant() switch{"buy"=>"buy","sell"=>"sell","cancellation"=>"cancellation","dividend"=>"dividend","interest"=>"interest","fee"=>"fee","tax"=>"tax","deposit"=>"deposit","withdrawal"=>"withdrawal","security_transfer_in"=>"security_transfer_in","security_transfer_out"=>"security_transfer_out","split"=>"split",_=>"other"};
}
