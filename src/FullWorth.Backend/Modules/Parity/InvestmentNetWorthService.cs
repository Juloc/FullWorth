using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record InvestmentNetWorthContribution(
    string BaseCurrency,
    decimal Amount,
    bool Incomplete,
    IReadOnlySet<Guid> ExcludedLinkedAccountIds);

public sealed class InvestmentNetWorthService(FullWorthDbContext db, CurrencyConverter currencyConverter)
{
    public async Task<InvestmentNetWorthContribution> CalculateAsync(
        Guid fullWorthSpaceId, Guid userId, DateOnly asOf, CancellationToken ct)
    {
        var space = await db.FullWorthSpaces.AsNoTracking()
            .Where(item => item.Id == fullWorthSpaceId &&
                           db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId))
            .Select(item => new { item.BaseCurrency })
            .SingleOrDefaultAsync(ct);
        if (space is null) return new("EUR", 0m, true, new HashSet<Guid>());

        var visibleAccounts = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var portfolios = await LoadPortfoliosAsync(fullWorthSpaceId, ct);
        var excluded = new HashSet<Guid>();
        var baseSnapshot = await currencyConverter.PrepareLatestAsync(space.BaseCurrency, asOf, ct);
        decimal totalBase = 0m;
        var incomplete = false;

        foreach (var portfolio in portfolios)
        {
            if (portfolio.AccountId.HasValue)
            {
                if (!visibleAccounts.Contains(portfolio.AccountId.Value)) continue;
                excluded.Add(portfolio.AccountId.Value);
            }

            var trades = await LoadTradesAsync(portfolio.Id, asOf, ct);
            var start = trades.Count == 0 ? asOf : trades.Min(item => item.TradeDate);
            var portfolioFx = await currencyConverter.PrepareAsync(portfolio.Currency, start, asOf, ct);
            decimal cash = 0m;
            var quantities = new Dictionary<Guid, decimal>();

            foreach (var trade in trades.OrderBy(item => item.TradeDate).ThenBy(item => item.CreatedAt).ThenBy(item => item.Id))
            {
                decimal? ToPortfolio(decimal amount) => portfolioFx.ToBaseOn(amount, trade.Currency, trade.TradeDate);
                var gross = trade.GrossAmount ?? (trade.Price.HasValue && trade.Quantity.HasValue
                    ? trade.Price.Value * trade.Quantity.Value
                    : trade.Amount);

                decimal? converted = trade.TradeType switch
                {
                    "deposit" => ToPortfolio(trade.Amount),
                    "withdrawal" => ToPortfolio(-trade.Amount),
                    "buy" => ToPortfolio(-(gross + trade.Fees + trade.Taxes + trade.WithholdingTax)),
                    "sell" => ToPortfolio(gross - trade.Fees - trade.Taxes - trade.WithholdingTax),
                    "dividend" or "interest" => ToPortfolio(trade.Amount - trade.Taxes - trade.WithholdingTax),
                    "fee" => ToPortfolio(-(trade.Amount + trade.Fees)),
                    "tax" => ToPortfolio(-(trade.Amount + trade.Taxes + trade.WithholdingTax)),
                    _ => 0m
                };
                if (!converted.HasValue) incomplete = true;
                else cash += converted.Value;

                if (!trade.SecurityId.HasValue) continue;
                var id = trade.SecurityId.Value;
                quantities.TryGetValue(id, out var quantity);
                quantity = trade.TradeType switch
                {
                    "buy" or "security_transfer_in" => quantity + (trade.Quantity ?? 0m),
                    "sell" or "security_transfer_out" => quantity - (trade.Quantity ?? 0m),
                    "split" when trade.Quantity is > 0 => quantity * trade.Quantity.Value,
                    _ => quantity
                };
                quantities[id] = quantity;
            }

            decimal securities = 0m;
            foreach (var holding in quantities.Where(item => item.Value > 0.0000000001m))
            {
                var price = await LatestPriceAsync(holding.Key, asOf, ct);
                if (price is null) { incomplete = true; continue; }
                var value = portfolioFx.ToBaseOn(price.Price * holding.Value, price.Currency, price.Date);
                if (!value.HasValue) { incomplete = true; continue; }
                securities += value.Value;
            }

            var portfolioTotal = cash + securities;
            var convertedBase = baseSnapshot.ToBaseOn(portfolioTotal, portfolio.Currency, asOf);
            if (!convertedBase.HasValue) incomplete = true;
            else totalBase += convertedBase.Value;
        }

        return new(space.BaseCurrency, totalBase, incomplete, excluded);
    }

    private async Task<List<PortfolioRow>> LoadPortfoliosAsync(Guid space, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Id","Currency","AccountId" FROM "InvestmentPortfolios"
WHERE "FullWorthSpaceId"=@space AND "IsArchived"=false AND "IncludeInNetWorth"=true
""", ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<PortfolioRow>();
        while (await reader.ReadAsync(ct)) result.Add(new(
            ParitySql.Guid(reader, "Id"), ParitySql.String(reader, "Currency"), ParitySql.NullableGuid(reader, "AccountId")));
        return result;
    }

    private async Task<List<TradeRow>> LoadTradesAsync(Guid portfolioId, DateOnly to, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Id","SecurityId","TradeType","TradeDate","Quantity","Price","GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","CreatedAt"
FROM "InvestmentTrades" WHERE "PortfolioId"=@portfolio AND "TradeDate"<=@to
ORDER BY "TradeDate","CreatedAt","Id"
""", ("@portfolio", portfolioId), ("@to", to));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<TradeRow>();
        while (await reader.ReadAsync(ct)) result.Add(new(
            ParitySql.Guid(reader, "Id"), ParitySql.NullableGuid(reader, "SecurityId"), ParitySql.String(reader, "TradeType"),
            ParitySql.NullableDate(reader, "TradeDate")!.Value, ParitySql.NullableDecimal(reader, "Quantity"),
            ParitySql.NullableDecimal(reader, "Price"), ParitySql.NullableDecimal(reader, "GrossAmount"),
            ParitySql.Decimal(reader, "Amount"), ParitySql.String(reader, "Currency"), ParitySql.Decimal(reader, "Fees"),
            ParitySql.Decimal(reader, "Taxes"), ParitySql.Decimal(reader, "WithholdingTax"), ParitySql.Timestamp(reader, "CreatedAt")));
        return result;
    }

    private async Task<PriceRow?> LatestPriceAsync(Guid securityId, DateOnly to, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "PriceDate","Price","Currency" FROM "SecurityPrices"
WHERE "SecurityId"=@security AND "PriceDate"<=@to
ORDER BY "PriceDate" DESC, CASE WHEN "Source"='manual' THEN 0 ELSE 1 END, "CreatedAt" DESC LIMIT 1
""", ("@security", securityId), ("@to", to));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(ParitySql.NullableDate(reader, "PriceDate")!.Value, ParitySql.Decimal(reader, "Price"), ParitySql.String(reader, "Currency"))
            : null;
    }

    private sealed record PortfolioRow(Guid Id, string Currency, Guid? AccountId);
    private sealed record TradeRow(Guid Id, Guid? SecurityId, string TradeType, DateOnly TradeDate, decimal? Quantity,
        decimal? Price, decimal? GrossAmount, decimal Amount, string Currency, decimal Fees, decimal Taxes,
        decimal WithholdingTax, DateTimeOffset CreatedAt);
    private sealed record PriceRow(DateOnly Date, decimal Price, string Currency);
}
