using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record PortfolioV2Write(
    string Name, string Currency, Guid? AccountId, Guid? BenchmarkSecurityId,
    string? ProviderName, bool IsManual = true, bool IncludeInNetWorth = true, bool IsArchived = false);
public sealed record InvestmentTradeV2Write(
    Guid? SecurityId, string TradeType, DateOnly TradeDate, DateOnly? SettlementDate,
    decimal? Quantity, decimal? Price, decimal? GrossAmount, decimal Amount, string Currency,
    decimal Fees = 0, decimal Taxes = 0, decimal WithholdingTax = 0,
    string Source = "manual", string? ExternalKey = null, string? Notes = null);
public sealed record BenchmarkWrite(string Name, Guid? SecurityId, string? ProviderSeriesKey);

public static class InvestmentCompletionParityEndpoints
{
    private static readonly HashSet<string> TradeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "buy", "sell", "dividend", "interest", "fee", "tax", "deposit", "withdrawal",
        "security_transfer_in", "security_transfer_out", "split", "other"
    };

    public static IEndpointRouteBuilder MapInvestmentCompletionParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/investments").WithTags("Investments");
        group.MapPut("/portfolios/{portfolioId:guid}/settings-v2", PutPortfolioV2);
        group.MapPost("/portfolios/{portfolioId:guid}/trades-v2", CreateTradeV2);
        group.MapGet("/portfolios/{portfolioId:guid}/overview-v2", OverviewV2);
        group.MapGet("/net-worth-contribution", NetWorthContribution);
        group.MapGet("/benchmarks", ListBenchmarks);
        group.MapPost("/benchmarks", CreateBenchmark);
        group.MapPut("/benchmarks/{benchmarkId:guid}", UpdateBenchmark);
        group.MapDelete("/benchmarks/{benchmarkId:guid}", DeleteBenchmark);
        return app;
    }

    private static async Task<IResult> PutPortfolioV2(
        Guid portfolioId, Guid fullWorthSpaceId, PortfolioV2Write request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "investments.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name) || !ValidCurrency(request.Currency))
            return Results.BadRequest(new { error = "Name and valid currency are required." });
        if (request.AccountId.HasValue)
        {
            var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
            if (!visible.Contains(request.AccountId.Value)) return Results.BadRequest(new { error = "Linked account is inaccessible." });
        }
        if (request.BenchmarkSecurityId.HasValue && !await SecurityExistsAsync(db, fullWorthSpaceId, request.BenchmarkSecurityId.Value, ct))
            return Results.BadRequest(new { error = "Benchmark security is invalid." });

        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
UPDATE "InvestmentPortfolios" SET
 "Name"=@name,"Currency"=@currency,"AccountId"=@account,"BenchmarkSecurityId"=@benchmark,
 "ProviderName"=@provider,"IsManual"=@manual,"IncludeInNetWorth"=@include,"IsArchived"=@archived,"UpdatedAt"=@now
WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""", ("@name", request.Name.Trim()), ("@currency", request.Currency.Trim().ToUpperInvariant()),
            ("@account", request.AccountId), ("@benchmark", request.BenchmarkSecurityId),
            ("@provider", string.IsNullOrWhiteSpace(request.ProviderName) ? null : request.ProviderName.Trim()),
            ("@manual", request.IsManual), ("@include", request.IncludeInNetWorth), ("@archived", request.IsArchived),
            ("@now", DateTimeOffset.UtcNow), ("@id", portfolioId), ("@space", fullWorthSpaceId));
        if (await command.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        audit.Record(fullWorthSpaceId, userId, "investment.portfolio.settings.updated", "InvestmentPortfolio", portfolioId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateTradeV2(
        Guid portfolioId, Guid fullWorthSpaceId, InvestmentTradeV2Write request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "investments.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await PortfolioExistsAsync(db, fullWorthSpaceId, portfolioId, ct)) return Results.NotFound();

        var type = request.TradeType.Trim().ToLowerInvariant();
        if (!TradeTypes.Contains(type)) return Results.BadRequest(new { error = "Unsupported investment transaction type." });
        if (!ValidCurrency(request.Currency) || request.Amount < 0 || request.Fees < 0 || request.Taxes < 0 || request.WithholdingTax < 0)
            return Results.BadRequest(new { error = "Amounts and currency are invalid." });
        if (request.SecurityId.HasValue && !await SecurityExistsAsync(db, fullWorthSpaceId, request.SecurityId.Value, ct))
            return Results.BadRequest(new { error = "Security is invalid." });

        if (type is "buy" or "sell" or "security_transfer_in" or "security_transfer_out")
        {
            if (!request.SecurityId.HasValue || request.Quantity is null or <= 0)
                return Results.BadRequest(new { error = "This transaction requires a security and positive quantity." });
        }
        if (type is "buy" or "sell" && request.Price is null or <= 0 && request.GrossAmount is null or <= 0)
            return Results.BadRequest(new { error = "Buy/sell requires a positive price or gross amount." });
        if (type == "split" && (!request.SecurityId.HasValue || request.Quantity is null or <= 0))
            return Results.BadRequest(new { error = "Split quantity stores the positive split ratio, e.g. 2 for 2:1." });
        if (type == "sell" && request.SecurityId.HasValue && request.Quantity.HasValue)
        {
            var owned = await OwnedQuantityAtAsync(db, portfolioId, request.SecurityId.Value, request.TradeDate, ct);
            if (request.Quantity.Value > owned + 0.0000000001m)
                return Results.Conflict(new { error = $"Cannot sell {request.Quantity.Value}; only {owned} units are owned on that date." });
        }

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
INSERT INTO "InvestmentTrades"
("Id","FullWorthSpaceId","PortfolioId","SecurityId","TradeType","TradeDate","SettlementDate","Quantity","Price","GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","Source","ExternalKey","Notes","CreatedAt","UpdatedAt")
VALUES (@id,@space,@portfolio,@security,@type,@tradeDate,@settlement,@quantity,@price,@gross,@amount,@currency,@fees,@taxes,@withholding,@source,@external,@notes,@now,@now)
""", ("@id", id), ("@space", fullWorthSpaceId), ("@portfolio", portfolioId), ("@security", request.SecurityId),
            ("@type", type), ("@tradeDate", request.TradeDate), ("@settlement", request.SettlementDate),
            ("@quantity", request.Quantity), ("@price", request.Price), ("@gross", request.GrossAmount),
            ("@amount", request.Amount), ("@currency", request.Currency.Trim().ToUpperInvariant()),
            ("@fees", request.Fees), ("@taxes", request.Taxes), ("@withholding", request.WithholdingTax),
            ("@source", NormalizeSource(request.Source)), ("@external", request.ExternalKey?.Trim()),
            ("@notes", request.Notes?.Trim()), ("@now", now));
        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception exception) when (exception.Message.Contains("Cannot sell", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = exception.Message });
        }
        audit.Record(fullWorthSpaceId, userId, "investment.trade.created", "InvestmentTrade", id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id });
    }

    private static async Task<IResult> OverviewV2(
        Guid portfolioId, Guid fullWorthSpaceId, DateOnly? asOf, CurrentUserContext currentUser,
        FullWorthDbContext db, CurrencyConverter fx, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var portfolio = await LoadPortfolioAsync(db, fullWorthSpaceId, portfolioId, ct);
        if (portfolio is null) return Results.NotFound();
        if (portfolio.AccountId.HasValue)
        {
            var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
            if (!visible.Contains(portfolio.AccountId.Value)) return Results.NotFound();
        }
        var day = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var calculation = await CalculatePortfolioAsync(db, fx, portfolio, day, ct);
        return Results.Ok(new
        {
            portfolio = new
            {
                portfolio.Id, portfolio.Name, portfolio.Currency, portfolio.AccountId, portfolio.BenchmarkSecurityId,
                portfolio.ProviderName, portfolio.IsManual, portfolio.IncludeInNetWorth, portfolio.IsArchived
            },
            asOf = day,
            marketValue = calculation.SecurityValue,
            cash = calculation.Cash,
            totalValue = calculation.TotalValue,
            realizedResult = calculation.RealizedResult,
            dividends = calculation.Dividends,
            incomplete = calculation.Incomplete,
            positions = calculation.Positions,
            stalePrices = calculation.Positions.Where(position => position.PriceState != "current").Select(position => new
            {
                position.SecurityId, position.Name, position.PriceDate, position.PriceState
            })
        });
    }

    private static async Task<IResult> NetWorthContribution(
        Guid fullWorthSpaceId, DateOnly? asOf, CurrentUserContext currentUser,
        FullWorthDbContext db, CurrencyConverter fx, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var day = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var portfolios = await LoadPortfoliosAsync(db, fullWorthSpaceId, includeArchived: false, ct);
        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var rows = new List<object>();
        decimal total = 0;
        var incomplete = false;
        var excludedLinkedAccounts = new List<Guid>();
        foreach (var portfolio in portfolios.Where(item => item.IncludeInNetWorth))
        {
            if (portfolio.AccountId.HasValue && !visible.Contains(portfolio.AccountId.Value)) continue;
            var value = await CalculatePortfolioAsync(db, fx, portfolio, day, ct);
            total += value.TotalValue;
            incomplete |= value.Incomplete;
            if (portfolio.AccountId.HasValue) excludedLinkedAccounts.Add(portfolio.AccountId.Value);
            rows.Add(new { portfolio.Id, portfolio.Name, portfolio.Currency, value = value.TotalValue, value.Incomplete });
        }
        return Results.Ok(new
        {
            asOf = day,
            total,
            incomplete,
            currencyMode = "portfolio-native-sum",
            excludedLinkedAccountIds = excludedLinkedAccounts.Distinct(),
            portfolios = rows
        });
    }

    private static async Task<IResult> ListBenchmarks(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Id","FullWorthSpaceId","Name","SecurityId","ProviderSeriesKey","IsBuiltIn","CreatedAt","UpdatedAt"
FROM "BenchmarkDefinitions" WHERE "FullWorthSpaceId" IS NULL OR "FullWorthSpaceId"=@space
ORDER BY "IsBuiltIn" DESC,"Name"
""", ("@space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct)) rows.Add(new
        {
            id = ParitySql.Guid(reader, "Id"),
            fullWorthSpaceId = ParitySql.NullableGuid(reader, "FullWorthSpaceId"),
            name = ParitySql.String(reader, "Name"),
            securityId = ParitySql.NullableGuid(reader, "SecurityId"),
            providerSeriesKey = ParitySql.NullableString(reader, "ProviderSeriesKey"),
            isBuiltIn = ParitySql.Bool(reader, "IsBuiltIn")
        });
        return Results.Ok(rows);
    }

    private static Task<IResult> CreateBenchmark(
        Guid fullWorthSpaceId, BenchmarkWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
        WriteBenchmark(Guid.NewGuid(), fullWorthSpaceId, request, currentUser, db, audit, update: false, ct);

    private static Task<IResult> UpdateBenchmark(
        Guid benchmarkId, Guid fullWorthSpaceId, BenchmarkWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
        WriteBenchmark(benchmarkId, fullWorthSpaceId, request, currentUser, db, audit, update: true, ct);

    private static async Task<IResult> WriteBenchmark(
        Guid id, Guid fullWorthSpaceId, BenchmarkWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, bool update, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "investments.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Name is required." });
        if (request.SecurityId.HasValue && !await SecurityExistsAsync(db, fullWorthSpaceId, request.SecurityId.Value, ct))
            return Results.BadRequest(new { error = "Security is invalid." });
        if (!request.SecurityId.HasValue && string.IsNullOrWhiteSpace(request.ProviderSeriesKey))
            return Results.BadRequest(new { error = "Choose a security or provider series." });
        var connection = await ParitySql.OpenAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        await using var command = update
            ? ParitySql.Command(connection, """
UPDATE "BenchmarkDefinitions" SET "Name"=@name,"SecurityId"=@security,"ProviderSeriesKey"=@series,"UpdatedAt"=@now
WHERE "Id"=@id AND "FullWorthSpaceId"=@space AND "IsBuiltIn"=false
""", ("@name", request.Name.Trim()), ("@security", request.SecurityId), ("@series", request.ProviderSeriesKey?.Trim()),
                ("@now", now), ("@id", id), ("@space", fullWorthSpaceId))
            : ParitySql.Command(connection, """
INSERT INTO "BenchmarkDefinitions" ("Id","FullWorthSpaceId","Name","SecurityId","ProviderSeriesKey","IsBuiltIn","CreatedAt","UpdatedAt")
VALUES (@id,@space,@name,@security,@series,false,@now,@now)
""", ("@id", id), ("@space", fullWorthSpaceId), ("@name", request.Name.Trim()),
                ("@security", request.SecurityId), ("@series", request.ProviderSeriesKey?.Trim()), ("@now", now));
        if (await command.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        audit.Record(fullWorthSpaceId, userId, update ? "investment.benchmark.updated" : "investment.benchmark.created", "BenchmarkDefinition", id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id });
    }

    private static async Task<IResult> DeleteBenchmark(
        Guid benchmarkId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "investments.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "DELETE FROM \"BenchmarkDefinitions\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"IsBuiltIn\"=false",
            ("@id", benchmarkId), ("@space", fullWorthSpaceId));
        if (await command.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
        audit.Record(fullWorthSpaceId, userId, "investment.benchmark.deleted", "BenchmarkDefinition", benchmarkId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<PortfolioCalculation> CalculatePortfolioAsync(
        FullWorthDbContext db, CurrencyConverter fx, PortfolioRow portfolio, DateOnly day, CancellationToken ct)
    {
        var trades = await LoadTradesAsync(db, portfolio.Id, day, ct);
        var securityIds = trades.Where(trade => trade.SecurityId.HasValue).Select(trade => trade.SecurityId!.Value).Distinct().ToArray();
        var securities = await LoadSecuritiesAsync(db, portfolio.FullWorthSpaceId, securityIds, ct);
        var prices = await LoadLatestPricesAsync(db, securityIds, day, ct);
        var snapshot = await fx.PrepareAsync(portfolio.Currency, trades.Count == 0 ? day : trades.Min(trade => trade.TradeDate), day, ct);
        var states = new Dictionary<Guid, PositionState>();
        decimal cash = 0;
        decimal dividends = 0;
        decimal realized = 0;
        var incomplete = false;

        foreach (var trade in trades.OrderBy(trade => trade.TradeDate).ThenBy(trade => trade.CreatedAt).ThenBy(trade => trade.Id))
        {
            decimal Convert(decimal amount)
            {
                var converted = snapshot.ToBaseOn(amount, trade.Currency, trade.TradeDate);
                if (!converted.HasValue) { incomplete = true; return 0; }
                return converted.Value;
            }
            var gross = trade.GrossAmount ?? (trade.Price.HasValue && trade.Quantity.HasValue ? trade.Price.Value * trade.Quantity.Value : trade.Amount);
            switch (trade.TradeType)
            {
                case "deposit": cash += Convert(trade.Amount); break;
                case "withdrawal": cash -= Convert(trade.Amount); break;
                case "interest": cash += Convert(trade.Amount - trade.Taxes - trade.WithholdingTax); break;
                case "fee": cash -= Convert(trade.Amount + trade.Fees); break;
                case "tax": cash -= Convert(trade.Amount + trade.Taxes + trade.WithholdingTax); break;
            }
            if (!trade.SecurityId.HasValue) continue;
            if (!states.TryGetValue(trade.SecurityId.Value, out var state))
                states[trade.SecurityId.Value] = state = new PositionState();
            switch (trade.TradeType)
            {
                case "buy":
                {
                    var cost = Convert(gross + trade.Fees + trade.Taxes + trade.WithholdingTax);
                    state.Quantity += trade.Quantity ?? 0;
                    state.CostBasis += cost;
                    cash -= cost;
                    break;
                }
                case "sell":
                {
                    var quantity = trade.Quantity ?? 0;
                    var avgCost = state.Quantity > 0 ? state.CostBasis / state.Quantity : 0;
                    var proceeds = Convert(gross - trade.Fees - trade.Taxes - trade.WithholdingTax);
                    realized += proceeds - avgCost * quantity;
                    state.CostBasis -= avgCost * quantity;
                    state.Quantity -= quantity;
                    cash += proceeds;
                    break;
                }
                case "security_transfer_in":
                    state.Quantity += trade.Quantity ?? 0;
                    state.CostBasisIncomplete = true;
                    break;
                case "security_transfer_out":
                {
                    var quantity = trade.Quantity ?? 0;
                    var avgCost = state.Quantity > 0 ? state.CostBasis / state.Quantity : 0;
                    state.CostBasis -= avgCost * quantity;
                    state.Quantity -= quantity;
                    break;
                }
                case "split":
                    if (trade.Quantity is > 0) state.Quantity *= trade.Quantity.Value;
                    break;
                case "dividend":
                {
                    var net = Convert(trade.Amount - trade.Taxes - trade.WithholdingTax);
                    dividends += net;
                    cash += net;
                    break;
                }
            }
        }

        var positions = new List<PositionView>();
        decimal securityValue = 0;
        foreach (var pair in states.Where(pair => pair.Value.Quantity > 0.0000000001m))
        {
            var security = securities.GetValueOrDefault(pair.Key);
            var state = pair.Value;
            prices.TryGetValue(pair.Key, out var price);
            decimal? market = null;
            var priceState = "missing";
            if (price is not null)
            {
                var converted = snapshot.ToBaseOn(price.Price * state.Quantity, price.Currency, price.Date);
                if (converted.HasValue)
                {
                    market = converted.Value;
                    securityValue += converted.Value;
                    var age = day.DayNumber - price.Date.DayNumber;
                    priceState = age <= 3 ? "current" : age <= 7 ? "recent" : "stale";
                }
                else incomplete = true;
            }
            else incomplete = true;
            var unrealized = market.HasValue && !state.CostBasisIncomplete ? market.Value - state.CostBasis : (decimal?)null;
            positions.Add(new PositionView(pair.Key, security?.Name ?? "Unknown", security?.AssetType ?? "other",
                state.Quantity, state.CostBasisIncomplete ? null : state.CostBasis,
                price?.Price, price?.Currency, price?.Date, priceState, market, unrealized, state.CostBasisIncomplete));
        }

        return new PortfolioCalculation(securityValue, cash, securityValue + cash, realized, dividends, incomplete, positions);
    }

    private static async Task<decimal> OwnedQuantityAtAsync(
        FullWorthDbContext db, Guid portfolioId, Guid securityId, DateOnly date, CancellationToken ct)
    {
        var trades = await LoadTradesAsync(db, portfolioId, date, ct, securityId);
        decimal quantity = 0;
        foreach (var trade in trades.OrderBy(trade => trade.TradeDate).ThenBy(trade => trade.CreatedAt).ThenBy(trade => trade.Id))
        {
            switch (trade.TradeType)
            {
                case "buy": case "security_transfer_in": quantity += trade.Quantity ?? 0; break;
                case "sell": case "security_transfer_out": quantity -= trade.Quantity ?? 0; break;
                case "split" when trade.Quantity is > 0: quantity *= trade.Quantity.Value; break;
            }
        }
        return quantity;
    }

    private static async Task<List<PortfolioRow>> LoadPortfoliosAsync(FullWorthDbContext db, Guid space, bool includeArchived, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        var sql = """
SELECT "Id","FullWorthSpaceId","Name","Currency","AccountId","BenchmarkSecurityId","ProviderName","IsManual","IncludeInNetWorth","IsArchived"
FROM "InvestmentPortfolios" WHERE "FullWorthSpaceId"=@space
""" + (includeArchived ? "" : " AND \"IsArchived\"=false");
        await using var command = ParitySql.Command(connection, sql, ("@space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<PortfolioRow>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadPortfolio(reader));
        return rows;
    }

    private static async Task<PortfolioRow?> LoadPortfolioAsync(FullWorthDbContext db, Guid space, Guid id, CancellationToken ct) =>
        (await LoadPortfoliosAsync(db, space, includeArchived: true, ct)).SingleOrDefault(row => row.Id == id);

    private static async Task<List<TradeRowV2>> LoadTradesAsync(
        FullWorthDbContext db, Guid portfolioId, DateOnly to, CancellationToken ct, Guid? securityId = null)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        var sql = """
SELECT "Id","SecurityId","TradeType","TradeDate","SettlementDate","Quantity","Price","GrossAmount","Amount","Currency","Fees","Taxes","WithholdingTax","Source","CreatedAt"
FROM "InvestmentTrades" WHERE "PortfolioId"=@portfolio AND "TradeDate"<=@to
""" + (securityId.HasValue ? " AND \"SecurityId\"=@security" : "") + " ORDER BY \"TradeDate\",\"CreatedAt\",\"Id\"";
        await using var command = securityId.HasValue
            ? ParitySql.Command(connection, sql, ("@portfolio", portfolioId), ("@to", to), ("@security", securityId.Value))
            : ParitySql.Command(connection, sql, ("@portfolio", portfolioId), ("@to", to));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<TradeRowV2>();
        while (await reader.ReadAsync(ct)) rows.Add(new(
            ParitySql.Guid(reader, "Id"), ParitySql.NullableGuid(reader, "SecurityId"), ParitySql.String(reader, "TradeType"),
            ParitySql.NullableDate(reader, "TradeDate")!.Value, ParitySql.NullableDate(reader, "SettlementDate"),
            ParitySql.NullableDecimal(reader, "Quantity"), ParitySql.NullableDecimal(reader, "Price"),
            ParitySql.NullableDecimal(reader, "GrossAmount"), ParitySql.Decimal(reader, "Amount"),
            ParitySql.String(reader, "Currency"), ParitySql.Decimal(reader, "Fees"), ParitySql.Decimal(reader, "Taxes"),
            ParitySql.Decimal(reader, "WithholdingTax"), ParitySql.String(reader, "Source"), ParitySql.Timestamp(reader, "CreatedAt")));
        return rows;
    }

    private static async Task<Dictionary<Guid, SecurityRowV2>> LoadSecuritiesAsync(
        FullWorthDbContext db, Guid space, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var connection = await ParitySql.OpenAsync(db, ct);
        var rows = new Dictionary<Guid, SecurityRowV2>();
        foreach (var id in ids)
        {
            await using var command = ParitySql.Command(connection, """
SELECT "Id","Name","AssetType","Currency" FROM "Securities" WHERE "Id"=@id AND "FullWorthSpaceId"=@space
""", ("@id", id), ("@space", space));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) rows[id] = new(id, ParitySql.String(reader, "Name"), ParitySql.String(reader, "AssetType"), ParitySql.String(reader, "Currency"));
        }
        return rows;
    }

    private static async Task<Dictionary<Guid, PriceRow>> LoadLatestPricesAsync(
        FullWorthDbContext db, IReadOnlyCollection<Guid> securityIds, DateOnly to, CancellationToken ct)
    {
        var result = new Dictionary<Guid, PriceRow>();
        var connection = await ParitySql.OpenAsync(db, ct);
        foreach (var id in securityIds)
        {
            await using var command = ParitySql.Command(connection, """
SELECT "PriceDate","Price","Currency" FROM "SecurityPrices"
WHERE "SecurityId"=@id AND "PriceDate"<=@to ORDER BY "PriceDate" DESC,
 CASE WHEN "Source"='manual' THEN 0 ELSE 1 END,"CreatedAt" DESC LIMIT 1
""", ("@id", id), ("@to", to));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) result[id] = new(ParitySql.NullableDate(reader, "PriceDate")!.Value,
                ParitySql.Decimal(reader, "Price"), ParitySql.String(reader, "Currency"));
        }
        return result;
    }

    private static async Task<bool> SecurityExistsAsync(FullWorthDbContext db, Guid space, Guid id, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"Securities\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space)",
            ("@id", id), ("@space", space));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> PortfolioExistsAsync(FullWorthDbContext db, Guid space, Guid id, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT EXISTS(SELECT 1 FROM \"InvestmentPortfolios\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space)",
            ("@id", id), ("@space", space));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(ct));
    }

    private static PortfolioRow ReadPortfolio(System.Data.Common.DbDataReader reader) => new(
        ParitySql.Guid(reader, "Id"), ParitySql.Guid(reader, "FullWorthSpaceId"), ParitySql.String(reader, "Name"),
        ParitySql.String(reader, "Currency"), ParitySql.NullableGuid(reader, "AccountId"),
        ParitySql.NullableGuid(reader, "BenchmarkSecurityId"), ParitySql.NullableString(reader, "ProviderName"),
        ParitySql.Bool(reader, "IsManual"), ParitySql.Bool(reader, "IncludeInNetWorth"), ParitySql.Bool(reader, "IsArchived"));

    private static string NormalizeSource(string? source) => string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim().ToLowerInvariant() switch
    {
        "manual" => "manual", "import" => "import", "provider" => "provider", _ => "manual"
    };
    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(char.IsLetter);

    private sealed record PortfolioRow(Guid Id, Guid FullWorthSpaceId, string Name, string Currency, Guid? AccountId,
        Guid? BenchmarkSecurityId, string? ProviderName, bool IsManual, bool IncludeInNetWorth, bool IsArchived);
    private sealed record TradeRowV2(Guid Id, Guid? SecurityId, string TradeType, DateOnly TradeDate, DateOnly? SettlementDate,
        decimal? Quantity, decimal? Price, decimal? GrossAmount, decimal Amount, string Currency, decimal Fees, decimal Taxes,
        decimal WithholdingTax, string Source, DateTimeOffset CreatedAt);
    private sealed record SecurityRowV2(Guid Id, string Name, string AssetType, string Currency);
    private sealed record PriceRow(DateOnly Date, decimal Price, string Currency);
    private sealed class PositionState
    {
        public decimal Quantity { get; set; }
        public decimal CostBasis { get; set; }
        public bool CostBasisIncomplete { get; set; }
    }
    private sealed record PositionView(Guid SecurityId, string Name, string AssetType, decimal Quantity, decimal? CostBasis,
        decimal? Price, string? PriceCurrency, DateOnly? PriceDate, string PriceState, decimal? MarketValue,
        decimal? UnrealizedResult, bool CostBasisIncomplete);
    private sealed record PortfolioCalculation(decimal SecurityValue, decimal Cash, decimal TotalValue, decimal RealizedResult,
        decimal Dividends, bool Incomplete, IReadOnlyList<PositionView> Positions);
}