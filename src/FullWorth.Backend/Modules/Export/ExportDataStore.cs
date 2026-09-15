using System.Globalization;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Budgets;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Transactions;

using static FullWorth.Backend.Modules.Export.CsvCell;

namespace FullWorth.Backend.Modules.Export;

/// <summary>
/// Die Zeilen, die ein Export aus der Datenbank holt: Etiketten, ihre Zuordnung zu Buchungen und der
/// ganze Anlagenteil.
///
/// Sie standen bis 2026-09-15 ZWEIMAL als private statische Methoden in Endpunktdateien - einmal fuer
/// den CSV-Export, einmal fuer den xlsx-Export -, byte-gleiches SQL in beiden. Beide Formate zeigen
/// dieselben Daten; unterschiedlich ist nur, wie sie geschrieben werden.
///
/// Und beide hatten dieselben zwei Schleifen: eine Abfrage je Buchung fuer die Etiketten, eine je
/// Depot fuer die Handel. Bei einem Export ueber 100 000 Buchungen waren das 100 000 Runden.
/// </summary>
public sealed class ExportDataStore(FullWorthDbContext db)
{
    /// <summary>
    /// Die Sammlungen dieses Space - seit #124 mit allem, was eine Sammlung ausmacht.
    ///
    /// Vorher standen hier nur Id, Name und Farbe. Symbol, Beschreibung, Zeitraum und Status haetten
    /// im Export gefehlt, und ein Export, aus dem sich der Stand nicht wiederherstellen laesst, ist
    /// keiner.
    /// </summary>
    public async Task<List<string[]>> Tags(Guid space, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var result = Table(new[] { "Id", "Name", "Color", "Icon", "Description", "StartDate", "EndDate", "Status" });
        await using var command = RawSql.Command(connection,
            "SELECT \"Id\",\"Name\",\"Color\",\"Icon\",\"Description\",\"StartDate\",\"EndDate\",\"Status\" FROM \"FinanceTags\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"Name\"",
            ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new[]
            {
                RawSql.Guid(reader, "Id").ToString(),
                RawSql.String(reader, "Name"),
                RawSql.NullableString(reader, "Color") ?? "",
                RawSql.NullableString(reader, "Icon") ?? "",
                RawSql.NullableString(reader, "Description") ?? "",
                RawSql.NullableDate(reader, "StartDate")?.ToString("yyyy-MM-dd") ?? "",
                RawSql.NullableDate(reader, "EndDate")?.ToString("yyyy-MM-dd") ?? "",
                RawSql.String(reader, "Status")
            });
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
        // Eine Abfrage fuer alle Depots statt einer je Depot. Die PortfolioId kommt jetzt aus der
        // Zeile, weil die Schleifenvariable weggefallen ist.
        if (allowedPortfolioIds.Count > 0)
        {
            var sql = "SELECT \"Id\",\"PortfolioId\",\"SecurityId\",\"TradeType\",\"TradeDate\",\"SettlementDate\",\"Quantity\",\"Price\",\"GrossAmount\",\"Amount\",\"Currency\",\"Fees\",\"Taxes\",\"WithholdingTax\",\"Source\",\"ExternalKey\",\"Notes\" FROM \"InvestmentTrades\" WHERE \"PortfolioId\"=ANY(@portfolios)" +
                      (from.HasValue ? " AND \"TradeDate\">=@from" : "") + (to.HasValue ? " AND \"TradeDate\"<=@to" : "") + " ORDER BY \"PortfolioId\",\"TradeDate\",\"Id\"";
            var parameters = new List<(string, object?)> { ("@portfolios", allowedPortfolioIds.ToArray()) };
            if (from.HasValue) parameters.Add(("@from", from.Value));
            if (to.HasValue) parameters.Add(("@to", to.Value));
            await using var command = RawSql.Command(connection, sql, parameters.ToArray());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                trades.Add(new[] { RawSql.Guid(reader, "Id").ToString(), RawSql.Guid(reader, "PortfolioId").ToString(), RawSql.NullableGuid(reader, "SecurityId")?.ToString() ?? "", RawSql.String(reader, "TradeType"), Date(RawSql.NullableDate(reader, "TradeDate")), Date(RawSql.NullableDate(reader, "SettlementDate")), RawSql.NullableDecimal(reader, "Quantity")?.ToString(CultureInfo.InvariantCulture) ?? "", RawSql.NullableDecimal(reader, "Price")?.ToString(CultureInfo.InvariantCulture) ?? "", RawSql.NullableDecimal(reader, "GrossAmount")?.ToString(CultureInfo.InvariantCulture) ?? "", Num(RawSql.Decimal(reader, "Amount")), RawSql.String(reader, "Currency"), Num(RawSql.Decimal(reader, "Fees")), Num(RawSql.Decimal(reader, "Taxes")), Num(RawSql.Decimal(reader, "WithholdingTax")), RawSql.String(reader, "Source"), RawSql.NullableString(reader, "ExternalKey") ?? "", RawSql.NullableString(reader, "Notes") ?? "" });
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

    /// <summary>
    /// Die Kurse aller Wertpapiere des Space, im Zeitfenster.
    ///
    /// Steht getrennt, weil nur der xlsx-Export sie mitnimmt. Der CSV-Export tut es nicht, und das zu
    /// aendern waere eine Produktentscheidung - hier wird nur umgeraeumt.
    /// </summary>
    public async Task<List<string[]>> SecurityPrices(
        Guid space, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var parameters = new List<(string, object?)> { ("@space", space) };
        if (from.HasValue) parameters.Add(("@from", from.Value));
        if (to.HasValue) parameters.Add(("@to", to.Value));

        var rows = Table(new[] { "SecurityId", "Date", "Price", "Currency", "Source" });
        await using var command = RawSql.Command(connection,
            "SELECT p.\"SecurityId\",p.\"PriceDate\",p.\"Price\",p.\"Currency\",p.\"Source\" FROM \"SecurityPrices\" p JOIN \"Securities\" s ON s.\"Id\"=p.\"SecurityId\" WHERE s.\"FullWorthSpaceId\"=@space"
            + (from.HasValue ? " AND p.\"PriceDate\">=@from" : "") + (to.HasValue ? " AND p.\"PriceDate\"<=@to" : ""),
            parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new[] { RawSql.Guid(reader, "SecurityId").ToString(), Date(RawSql.NullableDate(reader, "PriceDate")), Num(RawSql.Decimal(reader, "Price")), RawSql.String(reader, "Currency"), RawSql.String(reader, "Source") });
        return rows;
    }
/// <summary>Die Konten, die der Export zeigt - nur die sichtbaren, archivierte auf Wunsch.</summary>
    public Task<List<FinanceAccount>> AccountsAsync(
        IReadOnlySet<Guid> accountIds, bool includeArchived, CancellationToken ct) =>
        db.Accounts.AsNoTracking()
            .Where(account => accountIds.Contains(account.Id) && (includeArchived || account.IsActive))
            .OrderBy(account => account.SortOrder).ThenBy(account => account.DisplayName)
            .ToListAsync(ct);

    /// <summary>Die Buchungen dieser Konten im Zeitfenster, in stabiler Reihenfolge.</summary>
    public Task<List<FinanceTransaction>> TransactionsAsync(
        IReadOnlySet<Guid> accountIds, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var query = db.Transactions.AsNoTracking()
            .Where(transaction => accountIds.Contains(transaction.AccountId));
        if (from.HasValue)
            query = query.Where(transaction => (transaction.BookingDate ?? transaction.ValueDate) >= from.Value);
        if (to.HasValue)
            query = query.Where(transaction => (transaction.BookingDate ?? transaction.ValueDate) <= to.Value);
        return query
            .OrderBy(transaction => transaction.BookingDate ?? transaction.ValueDate)
            .ThenBy(transaction => transaction.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Die Kategorien des Space, zum Auffuellen der Namen. Archivierte nur, wenn der Aufrufer sie
    /// ausdruecklich will.
    /// </summary>
    public Task<List<FinanceCategory>> CategoriesAsync(
        Guid fullWorthSpaceId, bool includeArchived, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId
                            && (includeArchived || !category.IsArchived))
            .OrderBy(category => category.SortOrder).ThenBy(category => category.Name)
            .ToListAsync(ct);

    /// <summary>Die Aufteilungen zu genau diesen Buchungen - eine Buchung kann auf mehrere Kategorien gehen.</summary>
    public Task<List<TransactionAllocation>> AllocationsAsync(
        IReadOnlySet<Guid> transactionIds, CancellationToken ct) =>
        db.TransactionAllocations.AsNoTracking()
            .Where(allocation => transactionIds.Contains(allocation.TransactionId))
            .OrderBy(allocation => allocation.TransactionId).ThenBy(allocation => allocation.Id)
            .ToListAsync(ct);

    public Task<List<Budget>> BudgetsAsync(Guid fullWorthSpaceId, bool includeArchived, CancellationToken ct) =>
        db.Budgets.AsNoTracking()
            .Where(budget => budget.FullWorthSpaceId == fullWorthSpaceId && (includeArchived || budget.IsActive))
            .OrderBy(budget => budget.Name)
            .ToListAsync(ct);
}
