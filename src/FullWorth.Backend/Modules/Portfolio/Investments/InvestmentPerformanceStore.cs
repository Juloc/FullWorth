using FullWorth.Backend.Data;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>Ein Depot, so wie es in <c>InvestmentPortfolios</c> steht.</summary>
public sealed record PortfolioRow(
    Guid Id, Guid FullWorthSpaceId, string Name, string Currency, Guid? AccountId, Guid? BenchmarkSecurityId);

/// <summary>Ein Handel, so wie er in <c>InvestmentTrades</c> steht.</summary>
public sealed record TradeRow(
    Guid Id, Guid? SecurityId, string TradeType, DateOnly TradeDate, decimal? Quantity, decimal? Price,
    decimal? GrossAmount, decimal Amount, string Currency, decimal Fees, decimal Taxes, decimal WithholdingTax,
    DateTimeOffset CreatedAt);

/// <summary>Ein Kurs, so wie er in <c>SecurityPrices</c> steht.</summary>
public sealed record PriceRow(
    Guid SecurityId, DateOnly Date, decimal Price, string Currency, string Source, DateTimeOffset FetchedAt);

/// <summary>
/// Was die Wertentwicklung eines Depots an Rohdaten braucht: das Depot, seine Handel bis zum Stichtag
/// und die Kurse dazu.
///
/// Diese drei Abfragen standen bis 2026-09-15 als private statische Methoden in derselben Datei wie
/// der Endpunkt, jede mit dem DbContext als erstem Parameter. Sie rechnen nichts - das tut der
/// Endpunkt weiterhin selbst, und das ist richtig so: die Rechnung ist der Zweck der Route.
/// </summary>
public sealed class InvestmentPerformanceStore(FullWorthDbContext db)
{
    public async Task<PortfolioRow?> ReadPortfolioAsync(Guid fullWorthSpaceId, Guid portfolioId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId"
FROM "InvestmentPortfolios" WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""", ("@id", portfolioId), ("@space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PortfolioRow(
            RawSql.Guid(reader, "Id"),
            RawSql.Guid(reader, "FullWorthSpaceId"),
            RawSql.String(reader, "Name"),
            RawSql.String(reader, "Currency"),
            RawSql.NullableGuid(reader, "AccountId"),
            RawSql.NullableGuid(reader, "BenchmarkSecurityId"));
    }

    public async Task<List<TradeRow>> ListTradesAsync(Guid portfolioId, DateOnly end, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
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
                RawSql.Guid(reader, "Id"),
                RawSql.NullableGuid(reader, "SecurityId"),
                RawSql.String(reader, "TradeType"),
                RawSql.NullableDate(reader, "TradeDate")!.Value,
                RawSql.NullableDecimal(reader, "Quantity"),
                RawSql.NullableDecimal(reader, "Price"),
                RawSql.NullableDecimal(reader, "GrossAmount"),
                RawSql.Decimal(reader, "Amount"),
                RawSql.String(reader, "Currency"),
                RawSql.Decimal(reader, "Fees"),
                RawSql.Decimal(reader, "Taxes"),
                RawSql.Decimal(reader, "WithholdingTax"),
                RawSql.Timestamp(reader, "CreatedAt")));
        return rows;
    }

    public async Task<List<PriceRow>> ListPricesAsync(
        IReadOnlySet<Guid> securityIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (securityIds.Count == 0) return [];
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "SecurityId","PriceDate","Price","Currency","Source",COALESCE("FetchedAt","CreatedAt") AS "FetchedAt"
FROM "SecurityPrices"
WHERE "SecurityId"=ANY(@ids) AND "PriceDate">=@from AND "PriceDate"<=@to
ORDER BY "PriceDate","SecurityId"
""", ("@ids", securityIds.ToArray()), ("@from", from), ("@to", to));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<PriceRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new PriceRow(
                RawSql.Guid(reader, "SecurityId"),
                RawSql.NullableDate(reader, "PriceDate")!.Value,
                RawSql.Decimal(reader, "Price"),
                RawSql.String(reader, "Currency"),
                RawSql.String(reader, "Source"),
                RawSql.NullableTimestamp(reader, "FetchedAt") ?? DateTimeOffset.MinValue));
        return rows;
    }
}
