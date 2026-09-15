using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>Ein Depot in der Liste.</summary>
public sealed record PortfolioListRow(
    Guid Id, string Name, string Currency, Guid? AccountId, Guid? BenchmarkSecurityId, bool IsArchived,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Ein Wertpapier in der Liste.</summary>
public sealed record SecurityListRow(
    Guid Id, string Name, string? Isin, string? Wkn, string? Ticker, string AssetType, string Currency,
    string? Exchange);

/// <summary>Ein Handel in der Liste.</summary>
public sealed record TradeListRow(
    Guid Id, Guid? SecurityId, string TradeType, DateOnly? TradeDate, decimal? Quantity, decimal? Price,
    decimal Amount, string Currency, decimal Fees, decimal Taxes, string? ExternalKey, string? Notes);

/// <summary>Eine Ausschuettung.</summary>
public sealed record DividendRow(
    Guid Id, Guid? SecurityId, string? Security, DateOnly? Date, decimal Amount, string Currency, decimal Taxes);

/// <summary>Eine Merkliste.</summary>
public sealed record WatchlistRow(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Ein Eintrag einer Merkliste, mit dem zuletzt bekannten Kurs.</summary>
public sealed record WatchlistItemRow(
    Guid SecurityId, string Name, string? Ticker, string AssetType, decimal? TargetPrice, string? Notes,
    int SortOrder, decimal? Price, DateOnly? PriceDate, string? PriceCurrency);

/// <summary>
/// Depots, Wertpapiere, Handel, Kurse und Merklisten - der Datenzugriff hinter <c>/api/investments</c>.
///
/// Zwei Dinge, die man der Tabelle nicht ansieht:
///
/// Ein Depot ohne Konto gehoert allen Mitgliedern des Bereichs; erst ein verknuepftes Konto schraenkt
/// ein. Darum ist <see cref="PortfolioReadableAsync"/> dreiwertig gedacht - kein Depot, Depot ohne
/// Konto, Depot mit Konto - und nicht einfach "Konto sichtbar?".
///
/// Wertpapiere und Kurse haengen am Bereich, nicht am Depot. Die Bewertung laedt darum alle
/// Wertpapiere des Bereichs, zu dem das Depot gehoert, statt sie einzeln nachzuschlagen.
/// </summary>
public sealed class InvestmentStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<List<PortfolioListRow>> ListPortfoliosAsync(Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","Name","Currency","AccountId","BenchmarkSecurityId","IsArchived","CreatedAt","UpdatedAt"
FROM "InvestmentPortfolios" WHERE "FullWorthSpaceId"=@space ORDER BY "IsArchived","Name"
""", ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<PortfolioListRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new PortfolioListRow(
                RawSql.Guid(reader, "Id"), RawSql.String(reader, "Name"), RawSql.String(reader, "Currency"),
                RawSql.NullableGuid(reader, "AccountId"), RawSql.NullableGuid(reader, "BenchmarkSecurityId"),
                RawSql.Bool(reader, "IsArchived"), RawSql.Timestamp(reader, "CreatedAt"),
                RawSql.Timestamp(reader, "UpdatedAt")));
        return rows;
    }

    /// <summary>Anlegen oder aendern; false heisst, es gab nichts zu aendern.</summary>
    public async Task<bool> SavePortfolioBasicsAsync(
        Guid userId, Guid space, Guid id, PortfolioWrite request, bool update, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        var name = request.Name.Trim();
        var currency = request.Currency.Trim().ToUpperInvariant();

        await using var command = update
            ? RawSql.Command(connection, """
UPDATE "InvestmentPortfolios"
SET "Name"=@name,"Currency"=@currency,"AccountId"=@account,"BenchmarkSecurityId"=@benchmark,
    "IsArchived"=@archived,"UpdatedAt"=@now
WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""", ("@name", name), ("@currency", currency), ("@account", request.AccountId),
                ("@benchmark", request.BenchmarkSecurityId), ("@archived", request.IsArchived),
                ("@now", now), ("@id", id), ("@space", space))
            : RawSql.Command(connection, """
INSERT INTO "InvestmentPortfolios"
("Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId","IsArchived","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@currency,@account,@benchmark,@archived,@now,@now)
""", ("@id", id), ("@space", space), ("@name", name), ("@currency", currency),
                ("@account", request.AccountId), ("@benchmark", request.BenchmarkSecurityId),
                ("@archived", request.IsArchived), ("@now", now));

        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        audit.Record(space, userId, update ? "investment.portfolio.updated" : "investment.portfolio.created",
            "InvestmentPortfolio", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ArchivePortfolioAsync(Guid userId, Guid space, Guid id, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "UPDATE \"InvestmentPortfolios\" SET \"IsArchived\"=true,\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space",
            ("@now", DateTimeOffset.UtcNow), ("@id", id), ("@space", space));
        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;

        audit.Record(space, userId, "investment.portfolio.archived", "InvestmentPortfolio", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<SecurityListRow>> ListSecuritiesAsync(Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","Name","Isin","Wkn","Ticker","AssetType","Currency","Exchange"
FROM "Securities" WHERE "FullWorthSpaceId"=@space ORDER BY "Name"
""", ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<SecurityListRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new SecurityListRow(
                RawSql.Guid(reader, "Id"), RawSql.String(reader, "Name"), RawSql.NullableString(reader, "Isin"),
                RawSql.NullableString(reader, "Wkn"), RawSql.NullableString(reader, "Ticker"),
                RawSql.String(reader, "AssetType"), RawSql.String(reader, "Currency"),
                RawSql.NullableString(reader, "Exchange")));
        return rows;
    }

    /// <summary>
    /// Stammdaten eines Wertpapiers. Die Spalten, die nur die Verwaltungsoberflaeche kennt
    /// (Anbieterschluessel, aktiv/inaktiv), bleiben hier unberuehrt statt auf einen Vorgabewert
    /// zurueckgesetzt zu werden.
    /// </summary>
    public async Task<bool> SaveSecurityBasicsAsync(
        Guid userId, Guid space, Guid id, SecurityWrite request, string assetType, bool update, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        var name = request.Name.Trim();
        var isin = request.Isin?.Trim().ToUpperInvariant();
        var wkn = request.Wkn?.Trim().ToUpperInvariant();
        var ticker = request.Ticker?.Trim().ToUpperInvariant();
        var currency = request.Currency.Trim().ToUpperInvariant();
        var exchange = request.Exchange?.Trim();

        await using var command = update
            ? RawSql.Command(connection, """
UPDATE "Securities"
SET "Name"=@name,"Isin"=@isin,"Wkn"=@wkn,"Ticker"=@ticker,"AssetType"=@type,"Currency"=@currency,
    "Exchange"=@exchange,"UpdatedAt"=@now
WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""", ("@name", name), ("@isin", isin), ("@wkn", wkn), ("@ticker", ticker), ("@type", assetType),
                ("@currency", currency), ("@exchange", exchange), ("@now", now), ("@id", id), ("@space", space))
            : RawSql.Command(connection, """
INSERT INTO "Securities"
("Id","FullWorthSpaceId","Name","Isin","Wkn","Ticker","AssetType","Currency","Exchange","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@isin,@wkn,@ticker,@type,@currency,@exchange,@now,@now)
""", ("@id", id), ("@space", space), ("@name", name), ("@isin", isin), ("@wkn", wkn), ("@ticker", ticker),
                ("@type", assetType), ("@currency", currency), ("@exchange", exchange), ("@now", now));

        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        audit.Record(space, userId, update ? "investment.security.updated" : "investment.security.created",
            "Security", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<List<TradeListRow>> ListTradesAsync(Guid portfolioId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","SecurityId","TradeType","TradeDate","Quantity","Price","Amount","Currency","Fees","Taxes","ExternalKey","Notes"
FROM "InvestmentTrades" WHERE "PortfolioId"=@portfolio ORDER BY "TradeDate" DESC,"CreatedAt" DESC
""", ("@portfolio", portfolioId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<TradeListRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new TradeListRow(
                RawSql.Guid(reader, "Id"), RawSql.NullableGuid(reader, "SecurityId"),
                RawSql.String(reader, "TradeType"), RawSql.NullableDate(reader, "TradeDate"),
                RawSql.NullableDecimal(reader, "Quantity"), RawSql.NullableDecimal(reader, "Price"),
                RawSql.Decimal(reader, "Amount"), RawSql.String(reader, "Currency"),
                RawSql.Decimal(reader, "Fees"), RawSql.Decimal(reader, "Taxes"),
                RawSql.NullableString(reader, "ExternalKey"), RawSql.NullableString(reader, "Notes")));
        return rows;
    }

    public async Task<bool> DeleteTradeAsync(
        Guid userId, Guid space, Guid portfolioId, Guid id, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "DELETE FROM \"InvestmentTrades\" WHERE \"Id\"=@id AND \"PortfolioId\"=@portfolio AND \"FullWorthSpaceId\"=@space",
            ("@id", id), ("@portfolio", portfolioId), ("@space", space));
        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;

        audit.Record(space, userId, "investment.trade.deleted", "InvestmentTrade", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Ein Kurs je Wertpapier, Tag und Quelle. Ein zweiter Kurs derselben Quelle fuer denselben Tag
    /// ersetzt den ersten - eine Korrektur ist kein zweiter Kurs.
    /// </summary>
    public async Task SavePriceAsync(
        Guid userId, Guid space, Guid securityId, DateOnly date, decimal price, string currency, string source,
        CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
INSERT INTO "SecurityPrices" ("SecurityId","PriceDate","Price","Currency","Source","CreatedAt")
VALUES (@security,@date,@price,@currency,@source,@now)
ON CONFLICT ("SecurityId","PriceDate","Source")
DO UPDATE SET "Price"=EXCLUDED."Price","Currency"=EXCLUDED."Currency"
""", ("@security", securityId), ("@date", date), ("@price", price), ("@currency", currency),
            ("@source", source), ("@now", DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);

        audit.Record(space, userId, "investment.price.updated", "Security", securityId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<DividendRow>> ListDividendsAsync(Guid portfolioId, int? year, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var sql = """
SELECT t."Id",t."SecurityId",t."TradeDate",t."Amount",t."Currency",t."Taxes",s."Name"
FROM "InvestmentTrades" t LEFT JOIN "Securities" s ON s."Id"=t."SecurityId"
WHERE t."PortfolioId"=@portfolio AND t."TradeType"='dividend'
"""
            + (year.HasValue ? " AND EXTRACT(YEAR FROM t.\"TradeDate\")=@year" : string.Empty)
            + " ORDER BY t.\"TradeDate\" DESC";

        await using var command = year.HasValue
            ? RawSql.Command(connection, sql, ("@portfolio", portfolioId), ("@year", year.Value))
            : RawSql.Command(connection, sql, ("@portfolio", portfolioId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<DividendRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new DividendRow(
                RawSql.Guid(reader, "Id"), RawSql.NullableGuid(reader, "SecurityId"),
                RawSql.NullableString(reader, "Name"), RawSql.NullableDate(reader, "TradeDate"),
                RawSql.Decimal(reader, "Amount"), RawSql.String(reader, "Currency"),
                RawSql.Decimal(reader, "Taxes")));
        return rows;
    }

    public async Task<List<WatchlistRow>> ListWatchlistsAsync(Guid space, Guid userId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","Name","CreatedAt","UpdatedAt" FROM "Watchlists"
WHERE "FullWorthSpaceId"=@space AND "OwnerUserId"=@user ORDER BY "Name"
""", ("@space", space), ("@user", userId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<WatchlistRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new WatchlistRow(
                RawSql.Guid(reader, "Id"), RawSql.String(reader, "Name"),
                RawSql.Timestamp(reader, "CreatedAt"), RawSql.Timestamp(reader, "UpdatedAt")));
        return rows;
    }

    public async Task<bool> SaveWatchlistAsync(
        Guid userId, Guid space, Guid id, string name, bool update, string auditAction, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;

        await using var command = update
            ? RawSql.Command(connection,
                "UPDATE \"Watchlists\" SET \"Name\"=@name,\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"OwnerUserId\"=@user",
                ("@name", name.Trim()), ("@now", now), ("@id", id), ("@space", space), ("@user", userId))
            : RawSql.Command(connection, """
INSERT INTO "Watchlists" ("Id","FullWorthSpaceId","OwnerUserId","Name","CreatedAt","UpdatedAt")
VALUES (@id,@space,@user,@name,@now,@now)
""", ("@id", id), ("@space", space), ("@user", userId), ("@name", name.Trim()), ("@now", now));

        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        audit.Record(space, userId, auditAction, "Watchlist", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteWatchlistAsync(
        Guid userId, Guid space, Guid id, string auditAction, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "DELETE FROM \"Watchlists\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"OwnerUserId\"=@user",
            ("@id", id), ("@space", space), ("@user", userId));
        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;

        audit.Record(space, userId, auditAction, "Watchlist", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Eine Merkliste hat keine Aenderungsgeschichte: die neue Liste ersetzt die alte vollstaendig.
    /// Loeschen und Schreiben stehen darum in einer Transaktion - sonst waere die Liste zwischendurch
    /// leer, und ein Fehler beim Schreiben wuerde sie leer lassen.
    /// </summary>
    public async Task ReplaceWatchlistItemsAsync(
        Guid userId, Guid space, Guid watchlistId,
        IReadOnlyList<(Guid SecurityId, decimal? TargetPrice, string? Notes, int SortOrder)> items,
        string auditAction, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        await using (var delete = RawSql.Command(connection,
            "DELETE FROM \"WatchlistItems\" WHERE \"WatchlistId\"=@watchlist", ("@watchlist", watchlistId)))
            await delete.ExecuteNonQueryAsync(ct);

        foreach (var item in items)
        {
            await using var insert = RawSql.Command(connection, """
INSERT INTO "WatchlistItems" ("WatchlistId","SecurityId","TargetPrice","Notes","SortOrder")
VALUES (@watchlist,@security,@target,@notes,@sort)
""", ("@watchlist", watchlistId), ("@security", item.SecurityId), ("@target", item.TargetPrice),
                ("@notes", item.Notes), ("@sort", item.SortOrder));
            await insert.ExecuteNonQueryAsync(ct);
        }

        audit.Record(space, userId, auditAction, "Watchlist", watchlistId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Die Eintraege einer Merkliste mit dem zuletzt bekannten Kurs je Wertpapier. Das
    /// <c>LEFT JOIN LATERAL</c> holt genau eine Zeile je Wertpapier statt aller Kurse - ohne das
    /// waechst die Antwort mit der Kurshistorie.
    /// </summary>
    public async Task<List<WatchlistItemRow>> ListWatchlistItemsAsync(Guid watchlistId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT i."SecurityId",s."Name",s."Ticker",s."AssetType",i."TargetPrice",i."Notes",i."SortOrder",
       p."Price",p."PriceDate",p."Currency"
FROM "WatchlistItems" i
JOIN "Securities" s ON s."Id"=i."SecurityId"
LEFT JOIN LATERAL (
    SELECT "Price","PriceDate","Currency" FROM "SecurityPrices"
    WHERE "SecurityId"=s."Id" ORDER BY "PriceDate" DESC LIMIT 1) p ON true
WHERE i."WatchlistId"=@watchlist ORDER BY i."SortOrder",s."Name"
""", ("@watchlist", watchlistId));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<WatchlistItemRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new WatchlistItemRow(
                RawSql.Guid(reader, "SecurityId"), RawSql.String(reader, "Name"),
                RawSql.NullableString(reader, "Ticker"), RawSql.String(reader, "AssetType"),
                RawSql.NullableDecimal(reader, "TargetPrice"), RawSql.NullableString(reader, "Notes"),
                RawSql.Int(reader, "SortOrder"), RawSql.NullableDecimal(reader, "Price"),
                RawSql.NullableDate(reader, "PriceDate"), RawSql.NullableString(reader, "Currency")));
        return rows;
    }

    /// <summary>
    /// Ein Handel mit allem, was die Verwaltungsoberflaeche kennt: Valuta, Bruttobetrag,
    /// Quellensteuer und Herkunft.
    /// </summary>
    public async Task<bool> UpdateTradeAsync(
        Guid userId, Guid space, Guid portfolioId, Guid tradeId, InvestmentTradeV2Write request, string type,
        string source, string? externalKey, string? notes, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
UPDATE "InvestmentTrades"
SET "SecurityId"=@security,"TradeType"=@type,"TradeDate"=@date,"SettlementDate"=@settlement,
    "Quantity"=@quantity,"Price"=@price,"GrossAmount"=@gross,"Amount"=@amount,"Currency"=@currency,
    "Fees"=@fees,"Taxes"=@taxes,"WithholdingTax"=@withholding,"Source"=@source,"ExternalKey"=@external,
    "Notes"=@notes,"UpdatedAt"=@now
WHERE "Id"=@id AND "PortfolioId"=@portfolio AND "FullWorthSpaceId"=@space
""", ("@security", request.SecurityId), ("@type", type), ("@date", request.TradeDate),
            ("@settlement", request.SettlementDate), ("@quantity", request.Quantity), ("@price", request.Price),
            ("@gross", request.GrossAmount), ("@amount", request.Amount),
            ("@currency", request.Currency.Trim().ToUpperInvariant()), ("@fees", request.Fees),
            ("@taxes", request.Taxes), ("@withholding", request.WithholdingTax), ("@source", source),
            ("@external", externalKey), ("@notes", notes), ("@now", DateTimeOffset.UtcNow),
            ("@id", tradeId), ("@portfolio", portfolioId), ("@space", space));

        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        audit.Record(space, userId, "investment.trade.updated", "InvestmentTrade", tradeId);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SecurityExistsAsync(Guid space, Guid id, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"Securities\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space)",
            ("@id", id), ("@space", space));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Pruefen, ob eine ganze Auswahl von Wertpapieren zum Bereich gehoert - in einer Abfrage. Die
    /// Merkliste pruefte das frueher je Eintrag einzeln, und zwar blockierend.
    /// </summary>
    public async Task<bool> AllSecuritiesExistAsync(Guid space, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return true;
        var distinct = ids.Distinct().ToArray();
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT COUNT(*) FROM \"Securities\" WHERE \"Id\"=ANY(@ids) AND \"FullWorthSpaceId\"=@space",
            ("@ids", distinct), ("@space", space));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == distinct.Length;
    }

    public async Task<bool> PortfolioExistsAsync(Guid space, Guid id, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"InvestmentPortfolios\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space)",
            ("@id", id), ("@space", space));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    public async Task<bool> PortfolioReadableAsync(Guid userId, Guid space, Guid id, CancellationToken ct)
    {
        if (!await RawSql.IsMemberAsync(db, userId, space, ct)) return false;
        return await PortfolioAllowedAsync(
            await RawSql.VisibleAccountIdsAsync(db, userId, space, ct), space, id, ct);
    }

    public async Task<bool> PortfolioWritableAsync(Guid userId, Guid space, Guid id, CancellationToken ct)
    {
        if (!await CanManageAsync(userId, space, ct)) return false;
        return await PortfolioAllowedAsync(
            await RawSql.WritableAccountIdsAsync(db, userId, space, ct), space, id, ct);
    }

    public Task<bool> CanManageAsync(Guid userId, Guid space, CancellationToken ct) =>
        SpaceCapabilities.HasCapabilityAsync(db, userId, space, "investments.manage", ct);

    public async Task<bool> OwnsWatchlistAsync(Guid userId, Guid space, Guid id, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"Watchlists\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"OwnerUserId\"=@user)",
            ("@id", id), ("@space", space), ("@user", userId));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>Kein Depot heisst nein, ein Depot ohne Konto heisst ja, sonst entscheidet das Konto.</summary>
    private async Task<bool> PortfolioAllowedAsync(
        HashSet<Guid> accounts, Guid space, Guid id, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"AccountId\" FROM \"InvestmentPortfolios\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space",
            ("@id", id), ("@space", space));
        return await command.ExecuteScalarAsync(ct) switch
        {
            null => false,
            DBNull => true,
            Guid account => accounts.Contains(account),
            _ => false
        };
    }
}
