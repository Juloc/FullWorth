using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Parity;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Ingestion;

public sealed record FinTsHoldingSnapshotItem(
    string ProviderKey,
    string Name,
    string? Isin,
    string? Wkn,
    string Currency,
    decimal Quantity,
    decimal? Price,
    DateOnly? PriceDate,
    decimal? MarketValue,
    string? Exchange);

public sealed record FinTsInvestmentSnapshotRequest(
    Guid ConnectionId,
    string DepotKey,
    string Name,
    string Currency,
    DateOnly AsOf,
    IReadOnlyList<FinTsHoldingSnapshotItem> Holdings);

public static class FinTsInvestmentSnapshotEndpoints
{
    public static IEndpointRouteBuilder MapFinTsInvestmentSnapshotEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/banking/fints/investment-snapshot", IngestAsync)
            .WithTags("Internal banking");
        return app;
    }

    private static async Task<IResult> IngestAsync(
        FinTsInvestmentSnapshotRequest request,
        FullWorthDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DepotKey) || string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Trim().Length != 3)
            return Results.BadRequest();

        var connectionInfo = await db.BankConnections.AsNoTracking()
            .Where(x => x.Id == request.ConnectionId && x.Provider == "fints")
            .Select(x => new { x.FullWorthSpaceId, x.InstitutionName })
            .SingleOrDefaultAsync(ct);
        if (connectionInfo is null) return Results.NotFound();

        var spaceId = connectionInfo.FullWorthSpaceId;
        var providerName = $"fints:{request.ConnectionId:N}:{request.DepotKey.Trim()}";
        var now = DateTimeOffset.UtcNow;
        var sql = await ParitySql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        Guid portfolioId;

        await using (var findPortfolio = ParitySql.Command(sql,
            "SELECT \"Id\" FROM \"InvestmentPortfolios\" WHERE \"FullWorthSpaceId\"=@space AND \"ProviderName\"=@provider LIMIT 1",
            ("@space", spaceId), ("@provider", providerName)))
        await using (var reader = await findPortfolio.ExecuteReaderAsync(ct))
            portfolioId = await reader.ReadAsync(ct) ? ParitySql.Guid(reader, "Id") : Guid.Empty;

        if (portfolioId == Guid.Empty)
        {
            portfolioId = Guid.NewGuid();
            await using var createPortfolio = ParitySql.Command(sql, """
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId","ProviderName","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@currency,NULL,NULL,@provider,false,true,false,@now,@now)
""", ("@id", portfolioId), ("@space", spaceId), ("@name", request.Name.Trim()),
                ("@currency", request.Currency.Trim().ToUpperInvariant()), ("@provider", providerName), ("@now", now));
            await createPortfolio.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var updatePortfolio = ParitySql.Command(sql, """
UPDATE "InvestmentPortfolios" SET "Name"=@name,"Currency"=@currency,"IsArchived"=false,"IsManual"=false,
 "IncludeInNetWorth"=true,"UpdatedAt"=@now WHERE "Id"=@id
""", ("@name", request.Name.Trim()), ("@currency", request.Currency.Trim().ToUpperInvariant()),
                ("@now", now), ("@id", portfolioId));
            await updatePortfolio.ExecuteNonQueryAsync(ct);
        }

        var activeExternalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var holding in request.Holdings.Where(x => x.Quantity > 0))
        {
            var providerKey = holding.ProviderKey.Trim();
            if (providerKey.Length == 0 || holding.Name.Trim().Length == 0) continue;
            var securityId = Guid.Empty;

            await using (var findSecurity = ParitySql.Command(sql, """
SELECT "Id" FROM "Securities" WHERE "FullWorthSpaceId"=@space AND
 ((@isin IS NOT NULL AND "Isin"=@isin) OR "ProviderKey"=@providerKey) LIMIT 1
""", ("@space", spaceId), ("@isin", CleanUpper(holding.Isin)), ("@providerKey", providerKey)))
            await using (var reader = await findSecurity.ExecuteReaderAsync(ct))
                securityId = await reader.ReadAsync(ct) ? ParitySql.Guid(reader, "Id") : Guid.Empty;

            if (securityId == Guid.Empty)
            {
                securityId = Guid.NewGuid();
                await using var createSecurity = ParitySql.Command(sql, """
INSERT INTO "Securities"
("Id","FullWorthSpaceId","Name","Isin","Wkn","Ticker","AssetType","Currency","Exchange","ProviderKey","IsActive","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@isin,@wkn,NULL,'other',@currency,@exchange,@providerKey,true,@now,@now)
""", ("@id", securityId), ("@space", spaceId), ("@name", holding.Name.Trim()),
                    ("@isin", CleanUpper(holding.Isin)), ("@wkn", CleanUpper(holding.Wkn)),
                    ("@currency", NormalizeCurrency(holding.Currency, request.Currency)), ("@exchange", Clean(holding.Exchange)),
                    ("@providerKey", providerKey), ("@now", now));
                await createSecurity.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var updateSecurity = ParitySql.Command(sql, """
UPDATE "Securities" SET "Name"=@name,"Wkn"=COALESCE(@wkn,"Wkn"),"Currency"=@currency,
 "Exchange"=COALESCE(@exchange,"Exchange"),"ProviderKey"=@providerKey,"IsActive"=true,"UpdatedAt"=@now WHERE "Id"=@id
""", ("@name", holding.Name.Trim()), ("@wkn", CleanUpper(holding.Wkn)),
                    ("@currency", NormalizeCurrency(holding.Currency, request.Currency)), ("@exchange", Clean(holding.Exchange)),
                    ("@providerKey", providerKey), ("@now", now), ("@id", securityId));
                await updateSecurity.ExecuteNonQueryAsync(ct);
            }

            if (holding.Price is > 0)
            {
                var priceDate = holding.PriceDate ?? request.AsOf;
                await using var price = ParitySql.Command(sql, """
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt")
VALUES (@security,@date,@price,@currency,'fints',@now)
ON CONFLICT ("SecurityId","PriceDate","Source") DO UPDATE SET "Price"=EXCLUDED."Price","Currency"=EXCLUDED."Currency"
""", ("@security", securityId), ("@date", priceDate), ("@price", holding.Price.Value),
                    ("@currency", NormalizeCurrency(holding.Currency, request.Currency)), ("@now", now));
                await price.ExecuteNonQueryAsync(ct);
            }

            var externalKey = $"fints-position:{providerKey}";
            activeExternalKeys.Add(externalKey);
            var existingTradeId = Guid.Empty;
            await using (var findPosition = ParitySql.Command(sql,
                "SELECT \"Id\" FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio AND \"Source\"='fints_snapshot' AND \"ExternalKey\"=@external LIMIT 1",
                ("@portfolio", portfolioId), ("@external", externalKey)))
            await using (var reader = await findPosition.ExecuteReaderAsync(ct))
                existingTradeId = await reader.ReadAsync(ct) ? ParitySql.Guid(reader, "Id") : Guid.Empty;

            if (existingTradeId == Guid.Empty)
            {
                await using var position = ParitySql.Command(sql, """
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","SettlementDate","Quantity","Price","GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","Source","ExternalKey","Notes","CreatedAt","UpdatedAt")
VALUES (@id,@space,@portfolio,@security,'security_transfer_in',@date,NULL,@quantity,@price,@gross,0,@currency,0,0,0,'fints_snapshot',@external,NULL,@now,@now)
""", ("@id", Guid.NewGuid()), ("@space", spaceId), ("@portfolio", portfolioId), ("@security", securityId),
                    ("@date", request.AsOf), ("@quantity", holding.Quantity), ("@price", holding.Price),
                    ("@gross", holding.MarketValue), ("@currency", NormalizeCurrency(holding.Currency, request.Currency)),
                    ("@external", externalKey), ("@now", now));
                await position.ExecuteNonQueryAsync(ct);
            }
            else
            {
                await using var updatePosition = ParitySql.Command(sql, """
UPDATE "InvestmentTrades" SET "SecurityId"=@security,"TradeDate"=@date,"Quantity"=@quantity,"Price"=@price,
 "GrossAmount"=@gross,"Currency"=@currency,"UpdatedAt"=@now
WHERE "Id"=@id
""", ("@security", securityId), ("@date", request.AsOf), ("@quantity", holding.Quantity), ("@price", holding.Price),
                    ("@gross", holding.MarketValue), ("@currency", NormalizeCurrency(holding.Currency, request.Currency)),
                    ("@now", now), ("@id", existingTradeId));
                await updatePosition.ExecuteNonQueryAsync(ct);
            }
        }

        var existing = new List<(Guid Id, string Key)>();
        await using (var listPositions = ParitySql.Command(sql,
            "SELECT \"Id\",\"ExternalKey\" FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio AND \"Source\"='fints_snapshot'",
            ("@portfolio", portfolioId)))
        await using (var reader = await listPositions.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) existing.Add((ParitySql.Guid(reader, "Id"), ParitySql.NullableString(reader, "ExternalKey") ?? string.Empty));

        foreach (var stale in existing.Where(x => !activeExternalKeys.Contains(x.Key)))
        {
            await using var delete = ParitySql.Command(sql, "DELETE FROM \"InvestmentTrades\" WHERE \"Id\"=@id", ("@id", stale.Id));
            await delete.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return Results.Ok(new { portfolioId, positions = activeExternalKeys.Count });
    }

    private static string NormalizeCurrency(string? value, string fallback)
        => (string.IsNullOrWhiteSpace(value) ? fallback : value).Trim().ToUpperInvariant();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? CleanUpper(string? value) => Clean(value)?.ToUpperInvariant();
}
