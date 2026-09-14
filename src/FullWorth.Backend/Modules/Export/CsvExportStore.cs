using System.Globalization;
using FullWorth.Backend.Data;

using static FullWorth.Backend.Modules.Export.CsvCell;

namespace FullWorth.Backend.Modules.Export;

/// <summary>
/// Die Zeilen, die der CSV-Export aus der Datenbank holt: Etiketten, ihre Zuordnung zu Buchungen und
/// der ganze Anlagenteil.
///
/// Sie standen bis 2026-09-15 als private statische Methoden in der Endpunktdatei und bekamen die
/// offene Verbindung durchgereicht. Eine davon fragte je Buchung einzeln nach den Etiketten - bei
/// einem Export ueber 100 000 Buchungen waren das 100 000 Runden zur Datenbank.
/// </summary>
public sealed class CsvExportStore(FullWorthDbContext db)
{
    /// <summary>Die Etiketten dieses Space.</summary>
    public async Task<List<string[]>> Tags(Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var result = Table(new[] { "Id", "Name", "Color" });
        await using var command = RawSql.Command(connection, "SELECT \"Id\",\"Name\",\"Color\" FROM \"FinanceTags\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"Name\"", ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new[] { RawSql.Guid(reader, "Id").ToString(), RawSql.String(reader, "Name"), RawSql.NullableString(reader, "Color") ?? "" });
        return result;
    }

    /// <summary>
    /// Welche Etiketten an welcher Buchung haengen. Eine Abfrage fuer alle: vorher lief hier eine
    /// Schleife mit je einem SELECT pro Buchung.
    /// </summary>
    public async Task<List<string[]>> TransactionTags(IReadOnlySet<Guid> transactionIds, CancellationToken ct)
    {
        var result = Table(new[] { "TransactionId", "TagId" });
        if (transactionIds.Count == 0) return result;

        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection,
            "SELECT \"TransactionId\",\"TagId\" FROM \"TransactionTags\" WHERE \"TransactionId\"=ANY(@ids) ORDER BY \"TransactionId\",\"TagId\"",
            ("@ids", transactionIds.ToArray()));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new[] { RawSql.Guid(reader, "TransactionId").ToString(), RawSql.Guid(reader, "TagId").ToString() });
        return result;
    }

    /// <summary>Depots, Wertpapiere, Handel und Kurse - der ganze Anlagenteil des Exports.</summary>
    public async Task<Dictionary<string, List<string[]>>> Investments(
        Guid space,
        IReadOnlySet<Guid> visibleAccountIds,
        bool includeArchived,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var portfolios = Table(new[] { "Id", "Name", "ProviderName", "Currency", "LinkedAccountId", "BenchmarkSecurityId", "IsManual", "IncludeInNetWorth", "IsArchived" });
        var allowedPortfolioIds = new HashSet<Guid>();
        await using (var command = RawSql.Command(connection, "SELECT \"Id\",\"Name\",\"ProviderName\",\"Currency\",\"AccountId\",\"BenchmarkSecurityId\",\"IsManual\",\"IncludeInNetWorth\",\"IsArchived\" FROM \"InvestmentPortfolios\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"Name\"", ("@space", space)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var accountId = RawSql.NullableGuid(reader, "AccountId");
                var archived = RawSql.Bool(reader, "IsArchived");
                if (accountId.HasValue && !visibleAccountIds.Contains(accountId.Value)) continue;
                if (!includeArchived && archived) continue;
                var id = RawSql.Guid(reader, "Id");
                allowedPortfolioIds.Add(id);
                portfolios.Add(new[] { id.ToString(), RawSql.String(reader, "Name"), RawSql.NullableString(reader, "ProviderName") ?? "", RawSql.String(reader, "Currency"), accountId?.ToString() ?? "", RawSql.NullableGuid(reader, "BenchmarkSecurityId")?.ToString() ?? "", Bool(RawSql.Bool(reader, "IsManual")), Bool(RawSql.Bool(reader, "IncludeInNetWorth")), Bool(archived) });
            }
        }

        var trades = Table(new[] { "Id", "PortfolioId", "SecurityId", "TradeType", "TradeDate", "SettlementDate", "Quantity", "Price", "GrossAmount", "Amount", "Currency", "Fees", "Taxes", "WithholdingTax", "Source", "ExternalKey", "Notes" });
        foreach (var portfolioId in allowedPortfolioIds)
        {
            var sql = "SELECT \"Id\",\"SecurityId\",\"TradeType\",\"TradeDate\",\"SettlementDate\",\"Quantity\",\"Price\",\"GrossAmount\",\"Amount\",\"Currency\",\"Fees\",\"Taxes\",\"WithholdingTax\",\"Source\",\"ExternalKey\",\"Notes\" FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=@portfolio" +
                      (from.HasValue ? " AND \"TradeDate\">=@from" : "") + (to.HasValue ? " AND \"TradeDate\"<=@to" : "") + " ORDER BY \"TradeDate\",\"Id\"";
            var parameters = new List<(string, object?)> { ("@portfolio", portfolioId) };
            if (from.HasValue) parameters.Add(("@from", from.Value));
            if (to.HasValue) parameters.Add(("@to", to.Value));
            await using var command = RawSql.Command(connection, sql, parameters.ToArray());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                trades.Add(new[] { RawSql.Guid(reader, "Id").ToString(), portfolioId.ToString(), RawSql.NullableGuid(reader, "SecurityId")?.ToString() ?? "", RawSql.String(reader, "TradeType"), Date(RawSql.NullableDate(reader, "TradeDate")), Date(RawSql.NullableDate(reader, "SettlementDate")), RawSql.NullableDecimal(reader, "Quantity")?.ToString(CultureInfo.InvariantCulture) ?? "", RawSql.NullableDecimal(reader, "Price")?.ToString(CultureInfo.InvariantCulture) ?? "", RawSql.NullableDecimal(reader, "GrossAmount")?.ToString(CultureInfo.InvariantCulture) ?? "", Num(RawSql.Decimal(reader, "Amount")), RawSql.String(reader, "Currency"), Num(RawSql.Decimal(reader, "Fees")), Num(RawSql.Decimal(reader, "Taxes")), Num(RawSql.Decimal(reader, "WithholdingTax")), RawSql.String(reader, "Source"), RawSql.NullableString(reader, "ExternalKey") ?? "", RawSql.NullableString(reader, "Notes") ?? "" });
        }

        var securities = Table(new[] { "Id", "Name", "ISIN", "WKN", "Ticker", "AssetType", "Currency", "Exchange", "ProviderKey", "IsActive" });
        await using (var command = RawSql.Command(connection, "SELECT \"Id\",\"Name\",\"Isin\",\"Wkn\",\"Ticker\",\"AssetType\",\"Currency\",\"Exchange\",\"ProviderKey\",\"IsActive\" FROM \"Securities\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"Name\"", ("@space", space)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                securities.Add(new[] { RawSql.Guid(reader, "Id").ToString(), RawSql.String(reader, "Name"), RawSql.NullableString(reader, "Isin") ?? "", RawSql.NullableString(reader, "Wkn") ?? "", RawSql.NullableString(reader, "Ticker") ?? "", RawSql.String(reader, "AssetType"), RawSql.String(reader, "Currency"), RawSql.NullableString(reader, "Exchange") ?? "", RawSql.NullableString(reader, "ProviderKey") ?? "", Bool(RawSql.Bool(reader, "IsActive")) });

        return new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["investment_portfolios.csv"] = portfolios,
            ["investment_transactions.csv"] = trades,
            ["securities.csv"] = securities
        };
    }
}
