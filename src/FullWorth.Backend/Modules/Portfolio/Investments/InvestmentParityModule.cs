using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed record PortfolioWrite(string Name,string Currency,Guid? AccountId,Guid? BenchmarkSecurityId,bool IsArchived=false);
public sealed record SecurityWrite(string Name,string? Isin,string? Wkn,string? Ticker,string AssetType,string Currency,string? Exchange);
public sealed record TradeWrite(Guid? SecurityId,string TradeType,DateOnly TradeDate,decimal? Quantity,decimal? Price,decimal Amount,string Currency,decimal Fees=0,decimal Taxes=0,string? ExternalKey=null,string? Notes=null);
public sealed record PriceWrite(Guid SecurityId,DateOnly PriceDate,decimal Price,string Currency,string Source="manual");
public sealed record WatchlistWrite(string Name);
public sealed record WatchlistItemWrite(Guid SecurityId,decimal? TargetPrice,string? Notes);

public static class InvestmentParityEndpoints
{
    public static IEndpointRouteBuilder MapInvestmentParityEndpoints(this IEndpointRouteBuilder app)
    {
        var p=app.MapGroup("/api/investments").WithTags("Investments");
        p.MapGet("/portfolios",ListPortfolios);p.MapPost("/portfolios",CreatePortfolio);p.MapPut("/portfolios/{id:guid}",UpdatePortfolio);p.MapDelete("/portfolios/{id:guid}",ArchivePortfolio);
        p.MapGet("/securities",ListSecurities);p.MapPost("/securities",CreateSecurity);p.MapPut("/securities/{id:guid}",UpdateSecurity);
        p.MapGet("/portfolios/{portfolioId:guid}/trades",ListTrades);p.MapPost("/portfolios/{portfolioId:guid}/trades",CreateTrade);p.MapPut("/portfolios/{portfolioId:guid}/trades/{id:guid}",UpdateTrade);p.MapDelete("/portfolios/{portfolioId:guid}/trades/{id:guid}",DeleteTrade);
        p.MapPut("/prices",PutPrice);p.MapGet("/portfolios/{portfolioId:guid}/positions",Positions);p.MapGet("/portfolios/{portfolioId:guid}/performance",Performance);p.MapGet("/portfolios/{portfolioId:guid}/dividends",Dividends);
        p.MapGet("/watchlists",ListWatchlists);p.MapPost("/watchlists",CreateWatchlist);p.MapPut("/watchlists/{id:guid}",UpdateWatchlist);p.MapDelete("/watchlists/{id:guid}",DeleteWatchlist);p.MapPut("/watchlists/{id:guid}/items",PutWatchlistItems);p.MapGet("/watchlists/{id:guid}/items",GetWatchlistItems);
        return app;
    }

    private static async Task<IResult> ListPortfolios(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, InvestmentStore store,
        CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(uid, fullWorthSpaceId, ct)) return Results.NotFound();

        var rows = (await store.ListPortfoliosAsync(fullWorthSpaceId, ct))
            .Select(row => new
            {
                id = row.Id,
                name = row.Name,
                currency = row.Currency,
                accountId = row.AccountId,
                benchmarkSecurityId = row.BenchmarkSecurityId,
                isArchived = row.IsArchived,
                createdAt = row.CreatedAt,
                updatedAt = row.UpdatedAt
            });
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

    private static Task<IResult> CreateTrade(
        Guid portfolioId, Guid fullWorthSpaceId, TradeWrite request, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct) =>
        WriteTrade(Guid.NewGuid(), portfolioId, fullWorthSpaceId, request, currentUser, store, false, ct);

    private static Task<IResult> UpdateTrade(
        Guid id, Guid portfolioId, Guid fullWorthSpaceId, TradeWrite request, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct) =>
        WriteTrade(id, portfolioId, fullWorthSpaceId, request, currentUser, store, true, ct);

    private static async Task<IResult> WriteTrade(
        Guid id, Guid portfolioId, Guid fullWorthSpaceId, TradeWrite request, CurrentUserContext currentUser,
        InvestmentStore store, bool update, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.PortfolioWritableAsync(uid, fullWorthSpaceId, portfolioId, ct)) return Results.StatusCode(403);

        var type = NormalizeTradeType(request.TradeType);
        if (request.Amount < 0 || request.Fees < 0 || request.Taxes < 0 || request.Currency.Trim().Length != 3)
            return Results.BadRequest();
        if (type is "buy" or "sell" or "cancellation" or "security_transfer_in" or "security_transfer_out"
            && (request.SecurityId is null || request.Quantity is null or <= 0))
            return Results.BadRequest(new { error = "This trade type requires a security and positive quantity." });
        if (request.SecurityId.HasValue
            && !await store.SecurityExistsAsync(fullWorthSpaceId, request.SecurityId.Value, ct))
            return Results.BadRequest(new { error = "Security is invalid." });

        return await store.SaveTradeBasicsAsync(uid, fullWorthSpaceId, portfolioId, id, request, type, update, ct)
            ? Results.Ok(new { id })
            : Results.NotFound();
    }

    private static async Task<IResult> DeleteTrade(
        Guid id, Guid portfolioId, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentStore store,
        CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.PortfolioWritableAsync(uid, fullWorthSpaceId, portfolioId, ct)) return Results.StatusCode(403);

        return await store.DeleteTradeAsync(uid, fullWorthSpaceId, portfolioId, id, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> PutPrice(
        Guid fullWorthSpaceId, PriceWrite request, CurrentUserContext currentUser, InvestmentStore store,
        CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.CanManageAsync(uid, fullWorthSpaceId, ct)) return Results.StatusCode(403);
        if (request.Price <= 0 || request.Currency.Trim().Length != 3
            || !await store.SecurityExistsAsync(fullWorthSpaceId, request.SecurityId, ct))
            return Results.BadRequest();

        await store.SavePriceAsync(uid, fullWorthSpaceId, request.SecurityId, request.PriceDate, request.Price,
            request.Currency.Trim().ToUpperInvariant(),
            string.IsNullOrWhiteSpace(request.Source) ? "manual" : request.Source.Trim(), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Positions(
        Guid portfolioId, Guid fullWorthSpaceId, DateOnly? asOf, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.PortfolioReadableAsync(uid, fullWorthSpaceId, portfolioId, ct)) return Results.NotFound();

        var day = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var data = await store.LoadValuationAsync(portfolioId, day, ct);
        var positions = ComputePositions(data.Trades, data.Prices, day, data.Securities);

        return Results.Ok(new
        {
            asOf = day,
            positions,
            totalMarketValue = positions.Sum(x => x.MarketValue),
            totalCostBasis = positions.Sum(x => x.CostBasis),
            unrealizedGain = positions.Sum(x => x.UnrealizedGain)
        });
    }

    private static async Task<IResult> Performance(
        Guid portfolioId, Guid fullWorthSpaceId, DateOnly? from, DateOnly? to, CurrentUserContext currentUser,
        InvestmentStore store, CancellationToken ct)
    {
        var uid = currentUser.RequireUserId();
        if (!await store.PortfolioReadableAsync(uid, fullWorthSpaceId, portfolioId, ct)) return Results.NotFound();

        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? end.AddYears(-1);
        if (start > end) return Results.BadRequest();

        var data = await store.LoadValuationAsync(portfolioId, end, ct);

        // Die Kette bricht an jedem Tag, an dem sich etwas bewegt hat - sonst wuerde eine Einzahlung
        // wie ein Kursgewinn aussehen.
        var dates = data.Prices.Where(x => x.Date >= start && x.Date <= end).Select(x => x.Date)
            .Concat(data.Trades.Where(x => x.Date >= start && x.Date <= end).Select(x => x.Date))
            .Append(start).Append(end).Distinct().OrderBy(x => x).ToArray();

        decimal twr = 1m;
        decimal? previous = null;
        var points = new List<object>();
        foreach (var date in dates)
        {
            var value = ComputePositions(data.Trades, data.Prices, date, data.Securities).Sum(x => x.MarketValue);
            var external = ExternalFlow(data.Trades.Where(x => x.Date == date));
            if (previous.HasValue && previous.Value != 0) twr *= 1 + (value - external - previous.Value) / previous.Value;
            points.Add(new { date, value });
            previous = value;
        }

        var terminal = ComputePositions(data.Trades, data.Prices, end, data.Securities).Sum(x => x.MarketValue);
        var flows = new List<(DateOnly Date, decimal Amount)>();
        foreach (var trade in data.Trades.Where(x => x.Date >= start && x.Date <= end))
        {
            if (trade.Type == "deposit") flows.Add((trade.Date, -trade.Amount));
            else if (trade.Type == "withdrawal") flows.Add((trade.Date, trade.Amount));
        }
        if (terminal > 0) flows.Add((end, terminal));

        decimal? benchmark = null;
        if (data.BenchmarkSecurityId.HasValue)
        {
            var prices = data.Prices
                .Where(x => x.SecurityId == data.BenchmarkSecurityId.Value && x.Date >= start && x.Date <= end)
                .OrderBy(x => x.Date).ToArray();
            if (prices.Length >= 2 && prices[0].Price != 0) benchmark = prices[^1].Price / prices[0].Price - 1;
        }

        return Results.Ok(new
        {
            from = start,
            to = end,
            twr = dates.Length > 1 ? twr - 1 : (decimal?)null,
            xirr = Xirr(flows),
            benchmarkReturn = benchmark,
            marketValue = terminal,
            points
        });
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

    private sealed record PositionView(Guid SecurityId,string Name,string AssetType,decimal Quantity,decimal LastPrice,string Currency,decimal MarketValue,decimal CostBasis,decimal UnrealizedGain,decimal? UnrealizedPercent);

    /// <summary>
    /// Bestand und Einstandswert je Wertpapier zum Stichtag. Die Reihenfolge innerhalb eines Tages ist
    /// festgelegt (Kauf vor Split vor Verkauf), weil ein Verkauf vor dem Split eine andere Stueckzahl
    /// ergaebe als danach.
    /// </summary>
    private static List<PositionView> ComputePositions(
        List<InvestmentTradeData> trades, List<InvestmentPriceData> prices, DateOnly date,
        Dictionary<Guid, InvestmentSecurityData> securities)
    {
        var result = new List<PositionView>();
        foreach (var group in trades
            .Where(t => t.SecurityId.HasValue && t.Date <= date && t.Type is "buy" or "sell" or "cancellation"
                or "security_transfer_in" or "security_transfer_out" or "split")
            .GroupBy(t => t.SecurityId!.Value))
        {
            decimal quantity = 0, cost = 0;
            foreach (var trade in group.OrderBy(x => x.Date).ThenBy(x => InvestmentTradeOrder(x.Type)))
            {
                var amount = trade.Quantity ?? 0;
                if (trade.Type == "buy") { quantity += amount; cost += trade.Amount + trade.Fees + trade.Taxes; }
                else if (trade.Type == "security_transfer_in") quantity += amount;
                else if (trade.Type == "split" && amount > 0) quantity *= amount;
                else if (trade.Type is "sell" or "security_transfer_out" or "cancellation")
                {
                    var average = quantity == 0 ? 0 : cost / quantity;
                    quantity -= amount;
                    // Eine Stornierung nimmt den tatsaechlich gebuchten Betrag heraus, ein Verkauf den
                    // Durchschnittspreis der verkauften Stuecke.
                    cost = trade.Type == "cancellation"
                        ? Math.Max(0, cost - Math.Min(cost, trade.Amount + trade.Fees + trade.Taxes))
                        : Math.Max(0, cost - average * amount);
                }
            }
            if (quantity <= 0) continue;

            var security = securities[group.Key];
            var price = prices.Where(p => p.SecurityId == group.Key && p.Date <= date)
                    .OrderByDescending(p => p.Date).FirstOrDefault()?.Price
                ?? group.Where(t => t.Price.HasValue).OrderByDescending(t => t.Date).FirstOrDefault()?.Price
                ?? 0;
            var value = quantity * price;
            var gain = value - cost;
            result.Add(new PositionView(group.Key, security.Name, security.AssetType, quantity, price,
                security.Currency, value, cost, gain, cost == 0 ? null : gain / cost));
        }
        return result;
    }

    private static decimal ExternalFlow(IEnumerable<InvestmentTradeData> trades) =>
        trades.Sum(t => t.Type switch { "deposit" => t.Amount, "withdrawal" => -t.Amount, _ => 0 });

    private static decimal? Xirr(List<(DateOnly Date,decimal Amount)> flows){if(flows.Count<2||!flows.Any(x=>x.Amount<0)||!flows.Any(x=>x.Amount>0))return null;var first=flows.Min(x=>x.Date);double rate=.1;for(var iter=0;iter<100;iter++){double f=0,df=0;foreach(var flow in flows){var years=(flow.Date.DayNumber-first.DayNumber)/365.0;var denom=Math.Pow(1+rate,years);f+=(double)flow.Amount/denom;if(years!=0)df-=years*(double)flow.Amount/Math.Pow(1+rate,years+1);}if(Math.Abs(f)<1e-7)return(decimal)rate;if(Math.Abs(df)<1e-12)break;var next=rate-f/df;if(next<=-0.9999||double.IsNaN(next)||double.IsInfinity(next))break;if(Math.Abs(next-rate)<1e-9)return(decimal)next;rate=next;}return null;}

    private static string NormalizeAssetType(string? v)=>v?.Trim().ToLowerInvariant() switch{"etf"=>"etf","stock"=>"stock","fund"=>"fund","bond"=>"bond","crypto"=>"crypto","commodity"=>"commodity","metal"=>"commodity","derivative"=>"derivative","cash"=>"cash",_=>"other"};
    private static int InvestmentTradeOrder(string type)=>type switch{"buy" or "security_transfer_in"=>0,"split"=>1,"sell" or "security_transfer_out" or "cancellation"=>2,_=>3};
    private static string NormalizeTradeType(string? v)=>v?.Trim().ToLowerInvariant() switch{"buy"=>"buy","sell"=>"sell","cancellation"=>"cancellation","dividend"=>"dividend","interest"=>"interest","fee"=>"fee","tax"=>"tax","deposit"=>"deposit","withdrawal"=>"withdrawal","security_transfer_in"=>"security_transfer_in","security_transfer_out"=>"security_transfer_out","split"=>"split",_=>"other"};
}
