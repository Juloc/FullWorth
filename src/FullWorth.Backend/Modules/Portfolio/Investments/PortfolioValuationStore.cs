using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;

namespace FullWorth.Backend.Modules.Portfolio;

/// <summary>Ein Depot mit allen Einstellungen, die seine Bewertung beeinflussen.</summary>
public sealed record PortfolioSettingsRow(
    Guid Id, Guid FullWorthSpaceId, string Name, string Currency, Guid? AccountId, Guid? BenchmarkSecurityId,
    string? ProviderName, bool IsManual, bool IncludeInNetWorth, bool IsArchived);

/// <summary>Ein Handel mit allem, was die Bewertung braucht - Brutto, Gebuehren, Steuern, Quellensteuer.</summary>
public sealed record PortfolioTradeRow(
    Guid Id, Guid? SecurityId, string TradeType, DateOnly TradeDate, DateOnly? SettlementDate, decimal? Quantity,
    decimal? Price, decimal? GrossAmount, decimal Amount, string Currency, decimal Fees, decimal Taxes,
    decimal WithholdingTax, string Source, DateTimeOffset CreatedAt);

/// <summary>Ein Wertpapier, so weit die Bewertung es benennt.</summary>
public sealed record PortfolioSecurityRow(Guid Id, string Name, string AssetType, string Currency);

/// <summary>Der zuletzt bekannte Kurs eines Wertpapiers zu einem Stichtag.</summary>
public sealed record LatestPriceRow(DateOnly Date, decimal Price, string Currency);

/// <summary>Eine Vergleichsgroesse - entweder ein Wertpapier oder eine Reihe des Anbieters.</summary>
public sealed record BenchmarkRow(
    Guid Id, Guid? FullWorthSpaceId, string Name, Guid? SecurityId, string? ProviderSeriesKey, bool IsBuiltIn);

/// <summary>
/// Depoteinstellungen, Handel, Kurse und Vergleichsgroessen - die Rohdaten hinter der Depotuebersicht.
///
/// Zwei Abfragen liefen hier bis 2026-09-15 je Wertpapier einzeln: die Namen und der letzte Kurs. Ein
/// Depot mit 40 Titeln kostete damit 80 Abfragen, und die Vermoegensuebersicht multipliziert das mit
/// der Zahl der Depots. Beide holen jetzt alles in einem Zug; beim Kurs macht <c>DISTINCT ON</c> die
/// Auswahl, die vorher <c>LIMIT 1</c> je Abfrage war.
/// </summary>
public sealed class PortfolioValuationStore(FullWorthDbContext db, AuditService audit)
{
    public async Task<List<PortfolioSettingsRow>> ListPortfoliosAsync(
        Guid space, bool includeArchived, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var sql = """
SELECT "Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId","ProviderName","IsManual",
       "IncludeInNetWorth","IsArchived"
FROM "InvestmentPortfolios" WHERE "FullWorthSpaceId"=@space
""" + (includeArchived ? string.Empty : " AND \"IsArchived\"=false");

        await using var command = RawSql.Command(connection, sql, ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<PortfolioSettingsRow>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadPortfolio(reader));
        return rows;
    }

    public async Task<PortfolioSettingsRow?> FindPortfolioAsync(Guid space, Guid id, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId","ProviderName","IsManual",
       "IncludeInNetWorth","IsArchived"
FROM "InvestmentPortfolios" WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""", ("@id", id), ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadPortfolio(reader) : null;
    }

    public async Task<bool> SavePortfolioSettingsAsync(
        Guid userId, Guid space, Guid portfolioId, PortfolioSettingsWrite request, string? providerName,
        CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
UPDATE "InvestmentPortfolios" SET
 "Name"=@name,"Currency"=@currency,"AccountId"=@account,"BenchmarkSecurityId"=@benchmark,
 "ProviderName"=@provider,"IsManual"=@manual,"IncludeInNetWorth"=@include,"IsArchived"=@archived,"UpdatedAt"=@now
WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""", ("@name", request.Name.Trim()), ("@currency", request.Currency.Trim().ToUpperInvariant()),
            ("@account", request.AccountId), ("@benchmark", request.BenchmarkSecurityId),
            ("@provider", providerName), ("@manual", request.IsManual), ("@include", request.IncludeInNetWorth),
            ("@archived", request.IsArchived), ("@now", DateTimeOffset.UtcNow), ("@id", portfolioId),
            ("@space", space));

        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        audit.Record(space, userId, "investment.portfolio.settings.updated", "InvestmentPortfolio", portfolioId);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Guid> CreateTradeAsync(
        Guid userId, Guid space, Guid portfolioId, InvestmentTradeWrite request, string type, string source,
        string? externalKey, string? notes, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","SettlementDate","Quantity","Price",
 "GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","Source","ExternalKey","Notes","CreatedAt","UpdatedAt")
VALUES (@id,@space,@portfolio,@security,@type,@tradeDate,@settlement,@quantity,@price,@gross,@amount,@currency,
        @fees,@taxes,@withholding,@source,@external,@notes,@now,@now)
""", ("@id", id), ("@space", space), ("@portfolio", portfolioId), ("@security", request.SecurityId),
            ("@type", type), ("@tradeDate", request.TradeDate), ("@settlement", request.SettlementDate),
            ("@quantity", request.Quantity), ("@price", request.Price), ("@gross", request.GrossAmount),
            ("@amount", request.Amount), ("@currency", request.Currency.Trim().ToUpperInvariant()),
            ("@fees", request.Fees), ("@taxes", request.Taxes), ("@withholding", request.WithholdingTax),
            ("@source", source), ("@external", externalKey), ("@notes", notes), ("@now", DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(ct);

        audit.Record(space, userId, "investment.trade.created", "InvestmentTrade", id);
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>
    /// Die Handel eines Depots bis zum Stichtag, in der Reihenfolge, in der sie gewirkt haben.
    /// Die Sortierung ist Teil der Antwort und keine Bequemlichkeit: ein Verkauf vor dem Split ergibt
    /// eine andere Stueckzahl als danach.
    /// </summary>
    public async Task<List<PortfolioTradeRow>> ListTradesAsync(
        Guid portfolioId, DateOnly to, Guid? securityId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var sql = """
SELECT "Id","SecurityId","TradeType","TradeDate","SettlementDate","Quantity","Price","GrossAmount","Amount",
       "Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt"
FROM "InvestmentTrades" WHERE "PortfolioId"=@portfolio AND "TradeDate"<=@to
"""
            + (securityId.HasValue ? " AND \"SecurityId\"=@security" : string.Empty)
            + " ORDER BY \"TradeDate\",\"CreatedAt\",\"Id\"";

        await using var command = securityId.HasValue
            ? RawSql.Command(connection, sql, ("@portfolio", portfolioId), ("@to", to), ("@security", securityId.Value))
            : RawSql.Command(connection, sql, ("@portfolio", portfolioId), ("@to", to));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<PortfolioTradeRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new PortfolioTradeRow(
                RawSql.Guid(reader, "Id"), RawSql.NullableGuid(reader, "SecurityId"),
                RawSql.String(reader, "TradeType"), RawSql.NullableDate(reader, "TradeDate")!.Value,
                RawSql.NullableDate(reader, "SettlementDate"), RawSql.NullableDecimal(reader, "Quantity"),
                RawSql.NullableDecimal(reader, "Price"), RawSql.NullableDecimal(reader, "GrossAmount"),
                RawSql.Decimal(reader, "Amount"), RawSql.String(reader, "Currency"),
                RawSql.Decimal(reader, "Fees"), RawSql.Decimal(reader, "Taxes"),
                RawSql.Decimal(reader, "WithholdingTax"), RawSql.String(reader, "Source"),
                RawSql.Timestamp(reader, "CreatedAt")));
        return rows;
    }

    public async Task<Dictionary<Guid, PortfolioSecurityRow>> SecuritiesAsync(
        Guid space, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","Name","AssetType","Currency" FROM "Securities"
WHERE "Id"=ANY(@ids) AND "FullWorthSpaceId"=@space
""", ("@ids", ids.ToArray()), ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new Dictionary<Guid, PortfolioSecurityRow>();
        while (await reader.ReadAsync(ct))
        {
            var id = RawSql.Guid(reader, "Id");
            rows[id] = new PortfolioSecurityRow(id, RawSql.String(reader, "Name"),
                RawSql.String(reader, "AssetType"), RawSql.String(reader, "Currency"));
        }
        return rows;
    }

    /// <summary>
    /// Je Wertpapier der juengste Kurs bis zum Stichtag. Bei gleichem Tag gewinnt ein von Hand
    /// gesetzter Kurs vor einem geholten - wer korrigiert, will die Korrektur sehen.
    /// </summary>
    public async Task<Dictionary<Guid, LatestPriceRow>> LatestPricesAsync(
        IReadOnlyCollection<Guid> securityIds, DateOnly to, CancellationToken ct)
    {
        if (securityIds.Count == 0) return [];
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT DISTINCT ON ("SecurityId") "SecurityId","PriceDate","Price","Currency"
FROM "SecurityPrices" WHERE "SecurityId"=ANY(@ids) AND "PriceDate"<=@to
ORDER BY "SecurityId","PriceDate" DESC,CASE WHEN "Source"='manual' THEN 0 ELSE 1 END,"CreatedAt" DESC
""", ("@ids", securityIds.ToArray()), ("@to", to));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new Dictionary<Guid, LatestPriceRow>();
        while (await reader.ReadAsync(ct))
            rows[RawSql.Guid(reader, "SecurityId")] = new LatestPriceRow(
                RawSql.NullableDate(reader, "PriceDate")!.Value, RawSql.Decimal(reader, "Price"),
                RawSql.String(reader, "Currency"));
        return rows;
    }

    /// <summary>Die eigenen Vergleichsgroessen des Bereichs und die mitgelieferten.</summary>
    public async Task<List<BenchmarkRow>> ListBenchmarksAsync(Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","FullWorthSpaceId","Name","SecurityId","ProviderSeriesKey","IsBuiltIn"
FROM "BenchmarkDefinitions" WHERE "FullWorthSpaceId" IS NULL OR "FullWorthSpaceId"=@space
ORDER BY "IsBuiltIn" DESC,"Name"
""", ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<BenchmarkRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new BenchmarkRow(
                RawSql.Guid(reader, "Id"), RawSql.NullableGuid(reader, "FullWorthSpaceId"),
                RawSql.String(reader, "Name"), RawSql.NullableGuid(reader, "SecurityId"),
                RawSql.NullableString(reader, "ProviderSeriesKey"), RawSql.Bool(reader, "IsBuiltIn")));
        return rows;
    }

    /// <summary>Eine mitgelieferte Vergleichsgroesse gehoert keinem Bereich und bleibt unangetastet.</summary>
    public async Task<bool> SaveBenchmarkAsync(
        Guid userId, Guid space, Guid id, BenchmarkWrite request, bool update, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        var name = request.Name.Trim();
        var series = request.ProviderSeriesKey?.Trim();

        await using var command = update
            ? RawSql.Command(connection, """
UPDATE "BenchmarkDefinitions"
SET "Name"=@name,"SecurityId"=@security,"ProviderSeriesKey"=@series,"UpdatedAt"=@now
WHERE "Id"=@id AND "FullWorthSpaceId"=@space AND "IsBuiltIn"=false
""", ("@name", name), ("@security", request.SecurityId), ("@series", series), ("@now", now), ("@id", id),
                ("@space", space))
            : RawSql.Command(connection, """
INSERT INTO "BenchmarkDefinitions"
("Id","FullWorthSpaceId","Name","SecurityId","ProviderSeriesKey","IsBuiltIn","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@security,@series,false,@now,@now)
""", ("@id", id), ("@space", space), ("@name", name), ("@security", request.SecurityId), ("@series", series),
                ("@now", now));

        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        audit.Record(space, userId, update ? "investment.benchmark.updated" : "investment.benchmark.created",
            "BenchmarkDefinition", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteBenchmarkAsync(Guid userId, Guid space, Guid id, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "DELETE FROM \"BenchmarkDefinitions\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"IsBuiltIn\"=false",
            ("@id", id), ("@space", space));
        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;

        audit.Record(space, userId, "investment.benchmark.deleted", "BenchmarkDefinition", id);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static PortfolioSettingsRow ReadPortfolio(System.Data.Common.DbDataReader reader) => new(
        RawSql.Guid(reader, "Id"), RawSql.Guid(reader, "FullWorthSpaceId"), RawSql.String(reader, "Name"),
        RawSql.String(reader, "Currency"), RawSql.NullableGuid(reader, "AccountId"),
        RawSql.NullableGuid(reader, "BenchmarkSecurityId"), RawSql.NullableString(reader, "ProviderName"),
        RawSql.Bool(reader, "IsManual"), RawSql.Bool(reader, "IncludeInNetWorth"), RawSql.Bool(reader, "IsArchived"));
}
