using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed record PortfolioSettingsWrite(
    string Name, string Currency, Guid? AccountId, Guid? BenchmarkSecurityId,
    string? ProviderName, bool IsManual = true, bool IncludeInNetWorth = true, bool IsArchived = false);
public sealed record InvestmentTradeWrite(
    Guid? SecurityId, string TradeType, DateOnly TradeDate, DateOnly? SettlementDate,
    decimal? Quantity, decimal? Price, decimal? GrossAmount, decimal Amount, string Currency,
    decimal Fees = 0, decimal Taxes = 0, decimal WithholdingTax = 0,
    string Source = "manual", string? ExternalKey = null, string? Notes = null);
public sealed record BenchmarkWrite(string Name, Guid? SecurityId, string? ProviderSeriesKey);

public static class InvestmentPortfolioEndpoints
{
    private static readonly HashSet<string> TradeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "buy", "sell", "dividend", "interest", "fee", "tax", "deposit", "withdrawal",
        "security_transfer_in", "security_transfer_out", "split", "other"
    };

    public static IEndpointRouteBuilder MapInvestmentPortfolioEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/investments").WithTags("Investments");
        group.MapPut("/portfolios/{portfolioId:guid}/settings", PutPortfolioSettings);
        group.MapPost("/portfolios/{portfolioId:guid}/trades", CreateTrade);
        group.MapGet("/portfolios/{portfolioId:guid}/overview", PortfolioOverview);
        group.MapGet("/benchmarks", ListBenchmarks);
        group.MapPost("/benchmarks", CreateBenchmark);
        group.MapPut("/benchmarks/{benchmarkId:guid}", UpdateBenchmark);
        group.MapDelete("/benchmarks/{benchmarkId:guid}", DeleteBenchmark);
        return app;
    }

    private static async Task<IResult> PutPortfolioSettings(
        Guid portfolioId, Guid fullWorthSpaceId, PortfolioSettingsWrite request, CurrentUserContext currentUser,
        SpaceAccess space, InvestmentStore investments, PortfolioValuationStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await investments.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name) || !ValidCurrency(request.Currency))
            return Results.BadRequest(new { error = "Name and valid currency are required." });

        if (request.AccountId.HasValue)
        {
            var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
            if (!visible.Contains(request.AccountId.Value))
                return Results.BadRequest(new { error = "Linked account is inaccessible." });
        }
        if (request.BenchmarkSecurityId.HasValue
            && !await investments.SecurityExistsAsync(fullWorthSpaceId, request.BenchmarkSecurityId.Value, ct))
            return Results.BadRequest(new { error = "Benchmark security is invalid." });

        return await store.SavePortfolioSettingsAsync(userId, fullWorthSpaceId, portfolioId, request,
            Clean(request.ProviderName), ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> CreateTrade(
        Guid portfolioId, Guid fullWorthSpaceId, InvestmentTradeWrite request, CurrentUserContext currentUser,
        InvestmentStore investments, PortfolioValuationStore store, PortfolioValuationService valuation,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await investments.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await investments.PortfolioExistsAsync(fullWorthSpaceId, portfolioId, ct)) return Results.NotFound();

        var type = request.TradeType.Trim().ToLowerInvariant();
        if (!TradeTypes.Contains(type))
            return Results.BadRequest(new { error = "Unsupported investment transaction type." });
        if (!ValidCurrency(request.Currency) || request.Amount < 0 || request.Fees < 0 || request.Taxes < 0
            || request.WithholdingTax < 0)
            return Results.BadRequest(new { error = "Amounts and currency are invalid." });
        if (request.SecurityId.HasValue
            && !await investments.SecurityExistsAsync(fullWorthSpaceId, request.SecurityId.Value, ct))
            return Results.BadRequest(new { error = "Security is invalid." });

        if (type is "buy" or "sell" or "security_transfer_in" or "security_transfer_out"
            && (!request.SecurityId.HasValue || request.Quantity is null or <= 0))
            return Results.BadRequest(new { error = "This transaction requires a security and positive quantity." });
        if (type is "buy" or "sell" && request.Price is null or <= 0 && request.GrossAmount is null or <= 0)
            return Results.BadRequest(new { error = "Buy/sell requires a positive price or gross amount." });
        if (type == "split" && (!request.SecurityId.HasValue || request.Quantity is null or <= 0))
            return Results.BadRequest(new { error = "Split quantity stores the positive split ratio, e.g. 2 for 2:1." });

        // Verkauft wird, was an dem Tag da war - nicht, was heute da ist. Ein nachgetragener Verkauf
        // mit falschem Datum faellt hier auf und nicht erst in der Bewertung.
        if (type == "sell" && request.SecurityId.HasValue && request.Quantity.HasValue)
        {
            var owned = await valuation.OwnedQuantityAtAsync(
                portfolioId, request.SecurityId.Value, request.TradeDate, ct);
            if (request.Quantity.Value > owned + 0.0000000001m)
                return Results.Conflict(new
                {
                    error = $"Cannot sell {request.Quantity.Value}; only {owned} units are owned on that date."
                });
        }

        try
        {
            var id = await store.CreateTradeAsync(userId, fullWorthSpaceId, portfolioId, request, type,
                NormalizeSource(request.Source), Clean(request.ExternalKey), Clean(request.Notes), ct);
            return Results.Ok(new { id });
        }
        catch (Exception exception) when (exception.Message.Contains("Cannot sell", StringComparison.OrdinalIgnoreCase))
        {
            // Dieselbe Regel steht als Auslöser in der Datenbank - sie faengt auch den gleichzeitigen Fall.
            return Results.Conflict(new { error = exception.Message });
        }
    }

    private static async Task<IResult> PortfolioOverview(
        Guid portfolioId, Guid fullWorthSpaceId, DateOnly? asOf, CurrentUserContext currentUser,
        SpaceAccess space, PortfolioValuationStore store, PortfolioValuationService valuation, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var portfolio = await store.FindPortfolioAsync(fullWorthSpaceId, portfolioId, ct);
        if (portfolio is null) return Results.NotFound();
        if (portfolio.AccountId.HasValue)
        {
            var visible = await space.VisibleAccountIdsAsync(userId, fullWorthSpaceId, ct);
            if (!visible.Contains(portfolio.AccountId.Value)) return Results.NotFound();
        }

        var day = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var calculation = await valuation.CalculateAsync(portfolio, day, ct);

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
            stalePrices = calculation.Positions
                .Where(position => position.PriceState != "current")
                .Select(position => new { position.SecurityId, position.Name, position.PriceDate, position.PriceState })
        });
    }

    private static async Task<IResult> ListBenchmarks(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, SpaceAccess space, PortfolioValuationStore store,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var rows = (await store.ListBenchmarksAsync(fullWorthSpaceId, ct))
            .Select(row => new
            {
                id = row.Id,
                fullWorthSpaceId = row.FullWorthSpaceId,
                name = row.Name,
                securityId = row.SecurityId,
                providerSeriesKey = row.ProviderSeriesKey,
                isBuiltIn = row.IsBuiltIn
            });
        return Results.Ok(rows);
    }

    private static Task<IResult> CreateBenchmark(
        Guid fullWorthSpaceId, BenchmarkWrite request, CurrentUserContext currentUser, InvestmentStore investments,
        PortfolioValuationStore store, CancellationToken ct) =>
        WriteBenchmark(Guid.NewGuid(), fullWorthSpaceId, request, currentUser, investments, store, update: false, ct);

    private static Task<IResult> UpdateBenchmark(
        Guid benchmarkId, Guid fullWorthSpaceId, BenchmarkWrite request, CurrentUserContext currentUser,
        InvestmentStore investments, PortfolioValuationStore store, CancellationToken ct) =>
        WriteBenchmark(benchmarkId, fullWorthSpaceId, request, currentUser, investments, store, update: true, ct);

    private static async Task<IResult> WriteBenchmark(
        Guid id, Guid fullWorthSpaceId, BenchmarkWrite request, CurrentUserContext currentUser,
        InvestmentStore investments, PortfolioValuationStore store, bool update, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await investments.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Name is required." });
        if (request.SecurityId.HasValue
            && !await investments.SecurityExistsAsync(fullWorthSpaceId, request.SecurityId.Value, ct))
            return Results.BadRequest(new { error = "Security is invalid." });
        if (!request.SecurityId.HasValue && string.IsNullOrWhiteSpace(request.ProviderSeriesKey))
            return Results.BadRequest(new { error = "Choose a security or provider series." });

        return await store.SaveBenchmarkAsync(userId, fullWorthSpaceId, id, request, update, ct)
            ? Results.Ok(new { id })
            : Results.NotFound();
    }

    private static async Task<IResult> DeleteBenchmark(
        Guid benchmarkId, Guid fullWorthSpaceId, CurrentUserContext currentUser, InvestmentStore investments,
        PortfolioValuationStore store, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await investments.CanManageAsync(userId, fullWorthSpaceId, ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await store.DeleteBenchmarkAsync(userId, fullWorthSpaceId, benchmarkId, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static string NormalizeSource(string? source) => string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim().ToLowerInvariant() switch
    {
        "manual" => "manual", "import" => "import", "provider" => "provider", _ => "manual"
    };
    private static bool ValidCurrency(string? value) => value is { Length: 3 } && value.All(char.IsLetter);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
