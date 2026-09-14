using System.Data.Common;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Ingestion;

/// <summary>Was ein eingespielter Depotstand hinterlassen hat.</summary>
public sealed record FinTsSnapshotOutcome(Guid PortfolioId, int Positions);

/// <summary>
/// Ein Depotstand aus FinTS wird eingespielt: Depot anlegen oder auffrischen, je Position das
/// Wertpapier, seinen Kurs und die Position selbst, und am Ende faellt weg, was die Bank nicht mehr
/// meldet.
///
/// Das stand bis 2026-09-15 vollstaendig in einem HTTP-Handler - 165 Zeilen rohes SQL zwischen der
/// Anfragepruefung und dem Ergebnis. Es ist eine Transaktion ueber sechs Schritte; jeder hat jetzt
/// einen Namen, und die Transaktion umschliesst sie weiterhin alle. Bricht einer ab, ist kein halb
/// eingespielter Depotstand entstanden.
/// </summary>
public sealed class FinTsInvestmentSnapshotStore(FullWorthDbContext db)
{
    /// <summary>Der Space hinter der Bankverbindung - oder nichts, wenn es keine FinTS-Verbindung ist.</summary>
    public Task<Guid?> FindSpaceOfConnectionAsync(Guid connectionId, CancellationToken ct) =>
        db.BankConnections.AsNoTracking()
            .Where(connection => connection.Id == connectionId && connection.Provider == "fints")
            .Select(connection => (Guid?)connection.FullWorthSpaceId)
            .SingleOrDefaultAsync(ct);

    public async Task<FinTsSnapshotOutcome> ApplyAsync(
        Guid spaceId, FinTsInvestmentSnapshotRequest request, CancellationToken ct)
    {
        var providerName = $"fints:{request.ConnectionId:N}:{request.DepotKey.Trim()}";
        var now = DateTimeOffset.UtcNow;
        var sql = await RawSql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var portfolioId = await UpsertPortfolioAsync(sql, spaceId, providerName, request, now, ct);

        var activeExternalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var holding in request.Holdings.Where(holding => holding.Quantity > 0))
        {
            var providerKey = holding.ProviderKey.Trim();
            if (providerKey.Length == 0 || holding.Name.Trim().Length == 0) continue;

            var securityId = await UpsertSecurityAsync(sql, spaceId, providerKey, holding, request, now, ct);
            await UpsertPriceAsync(sql, securityId, holding, request, now, ct);

            var externalKey = $"fints-position:{providerKey}";
            activeExternalKeys.Add(externalKey);
            await UpsertPositionAsync(sql, spaceId, portfolioId, securityId, externalKey, holding, request, now, ct);
        }

        await RemoveStalePositionsAsync(sql, portfolioId, activeExternalKeys, ct);

        await transaction.CommitAsync(ct);
        return new FinTsSnapshotOutcome(portfolioId, activeExternalKeys.Count);
    }

    private static async Task<Guid> UpsertPortfolioAsync(
        DbConnection sql, Guid spaceId, string providerName,
        FinTsInvestmentSnapshotRequest request, DateTimeOffset now, CancellationToken ct)
    {
        Guid portfolioId;
        await using (var findPortfolio = RawSql.Command(sql,
            "SELECT \"Id\" FROM \"InvestmentPortfolios\" WHERE \"FullWorthSpaceId\"=@space AND \"ProviderName\"=@provider LIMIT 1",
            ("@space", spaceId), ("@provider", providerName)))
        await using (var reader = await findPortfolio.ExecuteReaderAsync(ct))
            portfolioId = await reader.ReadAsync(ct) ? RawSql.Guid(reader, "Id") : Guid.Empty;

        var currency = request.Currency.Trim().ToUpperInvariant();
        if (portfolioId == Guid.Empty)
        {
            portfolioId = Guid.NewGuid();
            await using var createPortfolio = RawSql.Command(sql, """
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId","ProviderName","IsManual","IncludeInNetWorth","IsArchived","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@currency,NULL,NULL,@provider,false,true,false,@now,@now)
""", ("@id", portfolioId), ("@space", spaceId), ("@name", request.Name.Trim()),
                ("@currency", currency), ("@provider", providerName), ("@now", now));
            await createPortfolio.ExecuteNonQueryAsync(ct);
            return portfolioId;
        }

        await using var updatePortfolio = RawSql.Command(sql, """
UPDATE "InvestmentPortfolios" SET "Name"=@name,"Currency"=@currency,"IsArchived"=false,"IsManual"=false,
 "IncludeInNetWorth"=true,"UpdatedAt"=@now WHERE "Id"=@id
""", ("@name", request.Name.Trim()), ("@currency", currency), ("@now", now), ("@id", portfolioId));
        await updatePortfolio.ExecuteNonQueryAsync(ct);
        return portfolioId;
    }

    private static async Task<Guid> UpsertSecurityAsync(
        DbConnection sql, Guid spaceId, string providerKey, FinTsHoldingSnapshotItem holding,
        FinTsInvestmentSnapshotRequest request, DateTimeOffset now, CancellationToken ct)
    {
        Guid securityId;
        await using (var findSecurity = RawSql.Command(sql, """
SELECT "Id" FROM "Securities" WHERE "FullWorthSpaceId"=@space AND
 -- Casts are required: an untyped NULL parameter leaves Postgres unable to infer a type for the
 -- IS NOT NULL test, so a holding WITHOUT an ISIN failed the whole depot snapshot with a 500.
 ((@isin::text IS NOT NULL AND "Isin"=@isin::text) OR "ProviderKey"=@providerKey) LIMIT 1
""", ("@space", spaceId), ("@isin", CleanUpper(holding.Isin)), ("@providerKey", providerKey)))
        await using (var reader = await findSecurity.ExecuteReaderAsync(ct))
            securityId = await reader.ReadAsync(ct) ? RawSql.Guid(reader, "Id") : Guid.Empty;

        var currency = NormalizeCurrency(holding.Currency, request.Currency);
        if (securityId == Guid.Empty)
        {
            securityId = Guid.NewGuid();
            await using var createSecurity = RawSql.Command(sql, """
INSERT INTO "Securities"
("Id","FullWorthSpaceId","Name","Isin","Wkn","Ticker","AssetType","Currency","Exchange","ProviderKey","IsActive","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@isin,@wkn,NULL,'other',@currency,@exchange,@providerKey,true,@now,@now)
""", ("@id", securityId), ("@space", spaceId), ("@name", holding.Name.Trim()),
                ("@isin", CleanUpper(holding.Isin)), ("@wkn", CleanUpper(holding.Wkn)),
                ("@currency", currency), ("@exchange", Clean(holding.Exchange)),
                ("@providerKey", providerKey), ("@now", now));
            await createSecurity.ExecuteNonQueryAsync(ct);
            return securityId;
        }

        await using var updateSecurity = RawSql.Command(sql, """
UPDATE "Securities" SET "Name"=@name,"Wkn"=COALESCE(@wkn,"Wkn"),"Currency"=@currency,
 "Exchange"=COALESCE(@exchange,"Exchange"),"ProviderKey"=@providerKey,"IsActive"=true,"UpdatedAt"=@now WHERE "Id"=@id
""", ("@name", holding.Name.Trim()), ("@wkn", CleanUpper(holding.Wkn)),
            ("@currency", currency), ("@exchange", Clean(holding.Exchange)),
            ("@providerKey", providerKey), ("@now", now), ("@id", securityId));
        await updateSecurity.ExecuteNonQueryAsync(ct);
        return securityId;
    }

    /// <summary>
    /// Eine Position ist in jeder Bewertung nur etwas wert, wenn es eine Kurszeile zu ihr gibt - und
    /// die Bank schickt nicht immer einen Stueckpreis, wohl aber den Kurswert der Position. Ohne das
    /// war die Position ueberall NICHTS wert, ein von der Bank mit 40 000 bewertetes Depot las sich
    /// als 0. Der abgeleitete Kurs ist der gemeldete Kurswert je Stueck, in derselben Waehrung.
    /// </summary>
    private static async Task UpsertPriceAsync(
        DbConnection sql, Guid securityId, FinTsHoldingSnapshotItem holding,
        FinTsInvestmentSnapshotRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var unitPrice = holding.Price is > 0
            ? holding.Price
            : holding.MarketValue is > 0 && holding.Quantity > 0
                ? decimal.Round(holding.MarketValue.Value / holding.Quantity, 10, MidpointRounding.ToEven)
                : null;
        if (unitPrice is not > 0) return;

        await using var price = RawSql.Command(sql, """
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt")
VALUES (@security,@date,@price,@currency,'fints',@now)
ON CONFLICT ("SecurityId","PriceDate","Source") DO UPDATE SET "Price"=EXCLUDED."Price","Currency"=EXCLUDED."Currency"
""", ("@security", securityId), ("@date", holding.PriceDate ?? request.AsOf), ("@price", unitPrice.Value),
            ("@currency", NormalizeCurrency(holding.Currency, request.Currency)), ("@now", now));
        await price.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertPositionAsync(
        DbConnection sql, Guid spaceId, Guid portfolioId, Guid securityId, string externalKey,
        FinTsHoldingSnapshotItem holding, FinTsInvestmentSnapshotRequest request, DateTimeOffset now,
        CancellationToken ct)
    {
        Guid existingTradeId;
        await using (var findPosition = RawSql.Command(sql,
            "SELECT \"Id\" FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio AND \"Source\"='fints_snapshot' AND \"ExternalKey\"=@external LIMIT 1",
            ("@portfolio", portfolioId), ("@external", externalKey)))
        await using (var reader = await findPosition.ExecuteReaderAsync(ct))
            existingTradeId = await reader.ReadAsync(ct) ? RawSql.Guid(reader, "Id") : Guid.Empty;

        var currency = NormalizeCurrency(holding.Currency, request.Currency);
        if (existingTradeId == Guid.Empty)
        {
            await using var position = RawSql.Command(sql, """
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","SettlementDate","Quantity","Price","GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","Source","ExternalKey","Notes","CreatedAt","UpdatedAt")
VALUES (@id,@space,@portfolio,@security,'security_transfer_in',@date,NULL,@quantity,@price,@gross,0,@currency,0,0,0,'fints_snapshot',@external,NULL,@now,@now)
""", ("@id", Guid.NewGuid()), ("@space", spaceId), ("@portfolio", portfolioId), ("@security", securityId),
                ("@date", request.AsOf), ("@quantity", holding.Quantity), ("@price", holding.Price),
                ("@gross", holding.MarketValue), ("@currency", currency),
                ("@external", externalKey), ("@now", now));
            await position.ExecuteNonQueryAsync(ct);
            return;
        }

        await using var updatePosition = RawSql.Command(sql, """
UPDATE "InvestmentTrades" SET "SecurityId"=@security,"TradeDate"=@date,"Quantity"=@quantity,"Price"=@price,
 "GrossAmount"=@gross,"Currency"=@currency,"UpdatedAt"=@now
WHERE "Id"=@id
""", ("@security", securityId), ("@date", request.AsOf), ("@quantity", holding.Quantity), ("@price", holding.Price),
            ("@gross", holding.MarketValue), ("@currency", currency), ("@now", now), ("@id", existingTradeId));
        await updatePosition.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Was die Bank nicht mehr meldet, gibt es im Depot nicht mehr.</summary>
    private static async Task RemoveStalePositionsAsync(
        DbConnection sql, Guid portfolioId, IReadOnlySet<string> activeExternalKeys, CancellationToken ct)
    {
        var existing = new List<(Guid Id, string Key)>();
        await using (var listPositions = RawSql.Command(sql,
            "SELECT \"Id\",\"ExternalKey\" FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio AND \"Source\"='fints_snapshot'",
            ("@portfolio", portfolioId)))
        await using (var reader = await listPositions.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                existing.Add((RawSql.Guid(reader, "Id"), RawSql.NullableString(reader, "ExternalKey") ?? string.Empty));

        foreach (var stale in existing.Where(row => !activeExternalKeys.Contains(row.Key)))
        {
            await using var delete = RawSql.Command(sql, "DELETE FROM \"InvestmentTrades\" WHERE \"Id\"=@id", ("@id", stale.Id));
            await delete.ExecuteNonQueryAsync(ct);
        }
    }

    private static string NormalizeCurrency(string? value, string fallback)
        => (string.IsNullOrWhiteSpace(value) ? fallback : value).Trim().ToUpperInvariant();
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? CleanUpper(string? value) => Clean(value)?.ToUpperInvariant();
}
