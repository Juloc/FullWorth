using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Parity;

public static class InvestmentPerformanceV2Endpoints
{
    public static IEndpointRouteBuilder MapInvestmentPerformanceV2Endpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/investments/portfolios/{portfolioId:guid}/performance-v2", GetPerformance)
            .WithTags("Investments");
        return app;
    }

    private static async Task<IResult> GetPerformance(
        Guid portfolioId,
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        CurrencyConverter converter,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var requestedStart = from ?? end.AddYears(-1);
        if (requestedStart > end) return Results.BadRequest(new { error = "From must not be after to." });

        var portfolio = await LoadPortfolioAsync(db, fullWorthSpaceId, portfolioId, ct);
        if (portfolio is null) return Results.NotFound();
        if (!await CanReadPortfolioAsync(db, userId, fullWorthSpaceId, portfolio.AccountId, ct)) return Results.NotFound();

        var trades = await LoadTradesAsync(db, portfolioId, end, ct);
        var securityIds = trades.Where(x => x.SecurityId.HasValue).Select(x => x.SecurityId!.Value).ToHashSet();
        if (portfolio.BenchmarkSecurityId.HasValue) securityIds.Add(portfolio.BenchmarkSecurityId.Value);
        var prices = await LoadPricesAsync(db, securityIds, requestedStart.AddDays(-14), end, ct);
        var earliestTrade = trades.Count == 0 ? requestedStart : trades.Min(x => x.TradeDate);
        var fx = await converter.PrepareAsync(portfolio.Currency, earliestTrade, end, ct);
        var data = new PerformanceData(portfolio, trades, prices, fx);

        var effectiveStart = requestedStart;
        var initial = ValueAt(data, requestedStart, excludeExternalFlowsOnDate: true);
        var startFlow = ExternalFlowAt(data, requestedStart);
        if (initial.Value + startFlow.Amount <= 0m)
        {
            var firstFunded = trades
                .Where(x => x.TradeDate >= requestedStart && x.TradeDate <= end && x.TradeType == "deposit")
                .OrderBy(x => x.TradeDate)
                .Select(x => x.TradeDate)
                .FirstOrDefault();
            if (firstFunded != default) effectiveStart = firstFunded;
        }

        var twrResult = CalculateTwr(data, effectiveStart, end);
        var terminal = ValueAt(data, end, excludeExternalFlowsOnDate: false);
        var xirr = CalculateXirr(data, effectiveStart, end, terminal.Value);
        var benchmark = BenchmarkReturn(data, effectiveStart, end);

        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reason in twrResult.Reasons) reasons.Add(reason);
        foreach (var reason in terminal.Reasons) reasons.Add(reason);
        foreach (var reason in benchmark.Reasons) reasons.Add(reason);

        var sampleDates = SampleDates(effectiveStart, end, 120);
        var points = new List<object>(sampleDates.Count);
        foreach (var date in sampleDates)
        {
            var value = ValueAt(data, date, excludeExternalFlowsOnDate: false);
            foreach (var reason in value.Reasons) reasons.Add(reason);
            var cumulative = CalculateTwr(data, effectiveStart, date);
            var benchmarkPoint = BenchmarkReturn(data, effectiveStart, date);
            points.Add(new
            {
                date,
                value = Math.Round(value.Value, 2),
                portfolioReturn = cumulative.Return,
                benchmarkReturn = benchmarkPoint.Return,
                incomplete = value.Incomplete || cumulative.Incomplete || benchmarkPoint.Incomplete
            });
        }

        return Results.Ok(new
        {
            from = requestedStart,
            effectiveFrom = effectiveStart,
            to = end,
            currency = portfolio.Currency,
            twr = twrResult.Return,
            xirr,
            benchmarkReturn = benchmark.Return,
            marketValue = Math.Round(terminal.Value, 2),
            incomplete = terminal.Incomplete || twrResult.Incomplete || benchmark.Incomplete,
            reasons = reasons.OrderBy(x => x),
            points
        });
    }

    private static TwrResult CalculateTwr(PerformanceData data, DateOnly start, DateOnly end)
    {
        if (start > end) return new(null, true, new HashSet<string>(StringComparer.Ordinal) { "invalid_range" });
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var externalDates = data.Trades
            .Where(x => x.TradeDate >= start && x.TradeDate <= end && x.TradeType is "deposit" or "withdrawal")
            .Select(x => x.TradeDate)
            .Distinct()
            .OrderBy(x => x)
            .ToList();
        if (!externalDates.Contains(start)) externalDates.Insert(0, start);

        var periods = new List<TwrSubperiod>();
        var incomplete = false;
        for (var i = 0; i < externalDates.Count; i++)
        {
            var periodStart = externalDates[i];
            var nextBoundary = i + 1 < externalDates.Count ? externalDates[i + 1] : (DateOnly?)null;
            var startValue = ValueAt(data, periodStart, excludeExternalFlowsOnDate: true);
            var flow = ExternalFlowAt(data, periodStart);
            var endValue = nextBoundary.HasValue
                ? ValueAt(data, nextBoundary.Value, excludeExternalFlowsOnDate: true)
                : ValueAt(data, end, excludeExternalFlowsOnDate: false);

            incomplete |= startValue.Incomplete || flow.Incomplete || endValue.Incomplete;
            foreach (var reason in startValue.Reasons) reasons.Add(reason);
            foreach (var reason in flow.Reasons) reasons.Add(reason);
            foreach (var reason in endValue.Reasons) reasons.Add(reason);

            // Ignore the pre-funding zero-capital period. Once capital exists, every period must be valid.
            if (periods.Count == 0 && startValue.Value + flow.Amount <= 0m)
                continue;
            periods.Add(new TwrSubperiod(startValue.Value, flow.Amount, endValue.Value));
        }

        var result = InvestmentPerformanceMath.TimeWeightedReturn(periods);
        if (!result.HasValue) reasons.Add("insufficient_performance_data");
        return new(result, incomplete || !result.HasValue, reasons);
    }

    private static decimal? CalculateXirr(PerformanceData data, DateOnly start, DateOnly end, decimal terminalValue)
    {
        var flows = new List<DatedCashFlow>();
        var startValue = ValueAt(data, start, excludeExternalFlowsOnDate: true).Value;
        if (startValue > 0m) flows.Add(new DatedCashFlow(start, -startValue));

        foreach (var group in data.Trades
                     .Where(x => x.TradeDate >= start && x.TradeDate <= end && x.TradeType is "deposit" or "withdrawal")
                     .GroupBy(x => x.TradeDate)
                     .OrderBy(x => x.Key))
        {
            var flow = ExternalFlowAt(data, group.Key);
            if (flow.Incomplete || flow.Amount == 0m) continue;
            // Investor perspective: deposits are outflows, withdrawals are inflows.
            flows.Add(new DatedCashFlow(group.Key, -flow.Amount));
        }

        if (terminalValue > 0m) flows.Add(new DatedCashFlow(end, terminalValue));
        return InvestmentPerformanceMath.Xirr(flows);
    }

    private static ValuationResult ValueAt(PerformanceData data, DateOnly date, bool excludeExternalFlowsOnDate)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var incomplete = false;
        decimal cash = 0m;
        var quantities = new Dictionary<Guid, decimal>();

        decimal Convert(decimal amount, string currency, DateOnly conversionDate)
        {
            var converted = data.Fx.ToBaseOn(amount, currency, conversionDate);
            if (converted.HasValue) return converted.Value;
            incomplete = true;
            reasons.Add("missing_fx");
            return 0m;
        }

        foreach (var trade in data.Trades
                     .Where(x => x.TradeDate <= date)
                     .OrderBy(x => x.TradeDate)
                     .ThenBy(x => x.CreatedAt)
                     .ThenBy(x => x.Id))
        {
            if (excludeExternalFlowsOnDate && trade.TradeDate == date && trade.TradeType is "deposit" or "withdrawal")
                continue;

            var gross = trade.GrossAmount ??
                        (trade.Price.HasValue && trade.Quantity.HasValue ? trade.Price.Value * trade.Quantity.Value : trade.Amount);
            switch (trade.TradeType)
            {
                case "deposit": cash += Convert(trade.Amount, trade.Currency, trade.TradeDate); break;
                case "withdrawal": cash -= Convert(trade.Amount, trade.Currency, trade.TradeDate); break;
                case "interest": cash += Convert(trade.Amount - trade.Taxes - trade.WithholdingTax, trade.Currency, trade.TradeDate); break;
                case "fee": cash -= Convert(trade.Amount + trade.Fees, trade.Currency, trade.TradeDate); break;
                case "tax": cash -= Convert(trade.Amount + trade.Taxes + trade.WithholdingTax, trade.Currency, trade.TradeDate); break;
            }

            if (!trade.SecurityId.HasValue) continue;
            quantities.TryGetValue(trade.SecurityId.Value, out var quantity);
            switch (trade.TradeType)
            {
                case "buy":
                    quantity += trade.Quantity ?? 0m;
                    cash -= Convert(gross + trade.Fees + trade.Taxes + trade.WithholdingTax, trade.Currency, trade.TradeDate);
                    break;
                case "sell":
                    quantity -= trade.Quantity ?? 0m;
                    cash += Convert(gross - trade.Fees - trade.Taxes - trade.WithholdingTax, trade.Currency, trade.TradeDate);
                    break;
                case "security_transfer_in": quantity += trade.Quantity ?? 0m; break;
                case "security_transfer_out": quantity -= trade.Quantity ?? 0m; break;
                case "split" when trade.Quantity is > 0m: quantity *= trade.Quantity.Value; break;
                case "dividend": cash += Convert(trade.Amount - trade.Taxes - trade.WithholdingTax, trade.Currency, trade.TradeDate); break;
            }
            quantities[trade.SecurityId.Value] = quantity;
        }

        decimal securityValue = 0m;
        foreach (var (securityId, quantity) in quantities.Where(x => x.Value > 0.0000000001m))
        {
            var price = EffectivePrice(data.Prices, securityId, date);
            if (price is null)
            {
                incomplete = true;
                reasons.Add("missing_price");
                continue;
            }

            var age = date.DayNumber - price.Date.DayNumber;
            if (age > 7)
            {
                incomplete = true;
                reasons.Add("stale_price");
            }
            securityValue += Convert(price.Price * quantity, price.Currency, date);
        }

        return new(cash + securityValue, incomplete, reasons);
    }

    private static FlowResult ExternalFlowAt(PerformanceData data, DateOnly date)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var incomplete = false;
        decimal total = 0m;
        foreach (var trade in data.Trades.Where(x => x.TradeDate == date && x.TradeType is "deposit" or "withdrawal"))
        {
            var converted = data.Fx.ToBaseOn(trade.Amount, trade.Currency, date);
            if (!converted.HasValue)
            {
                incomplete = true;
                reasons.Add("missing_fx");
                continue;
            }
            total += trade.TradeType == "deposit" ? converted.Value : -converted.Value;
        }
        return new(total, incomplete, reasons);
    }

    private static BenchmarkResult BenchmarkReturn(PerformanceData data, DateOnly start, DateOnly end)
    {
        if (!data.Portfolio.BenchmarkSecurityId.HasValue) return new(null, false, new HashSet<string>(StringComparer.Ordinal));
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var first = EffectivePrice(data.Prices, data.Portfolio.BenchmarkSecurityId.Value, start);
        var last = EffectivePrice(data.Prices, data.Portfolio.BenchmarkSecurityId.Value, end);
        if (first is null || last is null || first.Price <= 0m)
        {
            reasons.Add("benchmark_price_missing");
            return new(null, true, reasons);
        }
        var incomplete = false;
        if (start.DayNumber - first.Date.DayNumber > 7 || end.DayNumber - last.Date.DayNumber > 7)
        {
            incomplete = true;
            reasons.Add("benchmark_price_stale");
        }
        return new(last.Price / first.Price - 1m, incomplete, reasons);
    }

    private static PriceRow? EffectivePrice(IReadOnlyList<PriceRow> prices, Guid securityId, DateOnly date) =>
        prices.Where(x => x.SecurityId == securityId && x.Date <= date)
            .OrderByDescending(x => x.Date)
            .ThenBy(x => SourcePriority(x.Source))
            .ThenByDescending(x => x.FetchedAt)
            .FirstOrDefault();

    private static int SourcePriority(string source) => source.Trim().ToLowerInvariant() switch
    {
        "manual" => 0,
        "provider" => 1,
        _ => 2
    };

    private static IReadOnlyList<DateOnly> SampleDates(DateOnly start, DateOnly end, int maxPoints)
    {
        if (start >= end) return [end];
        var days = end.DayNumber - start.DayNumber;
        var step = Math.Max(1, (int)Math.Ceiling(days / (double)Math.Max(1, maxPoints - 1)));
        var result = new List<DateOnly>();
        for (var day = start; day < end; day = day.AddDays(step)) result.Add(day);
        if (result.Count == 0 || result[^1] != end) result.Add(end);
        return result;
    }

    private static async Task<bool> CanReadPortfolioAsync(
        FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, Guid? accountId, CancellationToken ct)
    {
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return false;
        if (!accountId.HasValue) return true;
        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        return visible.Contains(accountId.Value);
    }

    private static async Task<PortfolioRow?> LoadPortfolioAsync(
        FullWorthDbContext db, Guid fullWorthSpaceId, Guid portfolioId, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId"
FROM "InvestmentPortfolios" WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""", ("@id", portfolioId), ("@space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PortfolioRow(
            ParitySql.Guid(reader, "Id"),
            ParitySql.Guid(reader, "FullWorthSpaceId"),
            ParitySql.String(reader, "Name"),
            ParitySql.String(reader, "Currency"),
            ParitySql.NullableGuid(reader, "AccountId"),
            ParitySql.NullableGuid(reader, "BenchmarkSecurityId"));
    }

    private static async Task<List<TradeRow>> LoadTradesAsync(
        FullWorthDbContext db, Guid portfolioId, DateOnly end, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Id","SecurityId","TradeType","TradeDate","Quantity","Price","GrossAmount","Amount","Currency",
       "Fees","Taxes","WithholdingTax","CreatedAt"
FROM "InvestmentTrades"
WHERE "PortfolioId"=@portfolio AND "TradeDate"<=@end
ORDER BY "TradeDate","CreatedAt","Id"
""", ("@portfolio", portfolioId), ("@end", end));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<TradeRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new TradeRow(
                ParitySql.Guid(reader, "Id"),
                ParitySql.NullableGuid(reader, "SecurityId"),
                ParitySql.String(reader, "TradeType"),
                ParitySql.NullableDate(reader, "TradeDate")!.Value,
                ParitySql.NullableDecimal(reader, "Quantity"),
                ParitySql.NullableDecimal(reader, "Price"),
                ParitySql.NullableDecimal(reader, "GrossAmount"),
                ParitySql.Decimal(reader, "Amount"),
                ParitySql.String(reader, "Currency"),
                ParitySql.Decimal(reader, "Fees"),
                ParitySql.Decimal(reader, "Taxes"),
                ParitySql.Decimal(reader, "WithholdingTax"),
                ParitySql.Timestamp(reader, "CreatedAt")));
        return rows;
    }

    private static async Task<List<PriceRow>> LoadPricesAsync(
        FullWorthDbContext db, IReadOnlySet<Guid> securityIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (securityIds.Count == 0) return [];
        var ids = securityIds.ToArray();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "SecurityId","PriceDate","Price","Currency","Source",COALESCE("FetchedAt","CreatedAt") AS "FetchedAt"
FROM "SecurityPrices"
WHERE "SecurityId"=ANY(@ids) AND "PriceDate">=@from AND "PriceDate"<=@to
ORDER BY "PriceDate","SecurityId"
""", ("@ids", ids), ("@from", from), ("@to", to));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<PriceRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new PriceRow(
                ParitySql.Guid(reader, "SecurityId"),
                ParitySql.NullableDate(reader, "PriceDate")!.Value,
                ParitySql.Decimal(reader, "Price"),
                ParitySql.String(reader, "Currency"),
                ParitySql.String(reader, "Source"),
                ParitySql.NullableTimestamp(reader, "FetchedAt") ?? DateTimeOffset.MinValue));
        return rows;
    }

    private sealed record PortfolioRow(Guid Id, Guid FullWorthSpaceId, string Name, string Currency, Guid? AccountId, Guid? BenchmarkSecurityId);
    private sealed record TradeRow(
        Guid Id, Guid? SecurityId, string TradeType, DateOnly TradeDate, decimal? Quantity, decimal? Price,
        decimal? GrossAmount, decimal Amount, string Currency, decimal Fees, decimal Taxes, decimal WithholdingTax,
        DateTimeOffset CreatedAt);
    private sealed record PriceRow(Guid SecurityId, DateOnly Date, decimal Price, string Currency, string Source, DateTimeOffset FetchedAt);
    private sealed record PerformanceData(PortfolioRow Portfolio, List<TradeRow> Trades, List<PriceRow> Prices, FxSnapshot Fx);
    private sealed record ValuationResult(decimal Value, bool Incomplete, IReadOnlySet<string> Reasons);
    private sealed record FlowResult(decimal Amount, bool Incomplete, IReadOnlySet<string> Reasons);
    private sealed record TwrResult(decimal? Return, bool Incomplete, IReadOnlySet<string> Reasons);
    private sealed record BenchmarkResult(decimal? Return, bool Incomplete, IReadOnlySet<string> Reasons);
}
