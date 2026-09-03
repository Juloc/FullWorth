using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Parity;

public static class InvestmentNetWorthV2Endpoints
{
    public static IEndpointRouteBuilder MapInvestmentNetWorthV2Endpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/investments/net-worth-contribution-v2", GetContribution)
            .WithTags("Investments");
        return app;
    }

    private static async Task<IResult> GetContribution(
        Guid fullWorthSpaceId,
        DateOnly? asOf,
        CurrentUserContext currentUser,
        FullWorthDbContext db,
        InvestmentNetWorthService service,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var day = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var result = await service.CalculateAsync(fullWorthSpaceId, userId, day, ct);
        return Results.Ok(new
        {
            asOf = day,
            currency = result.BaseCurrency,
            total = Math.Round(result.Amount, 2),
            incomplete = result.Incomplete,
            currencyMode = "fullworth-space-base",
            excludedLinkedAccountIds = result.ExcludedLinkedAccountIds.OrderBy(id => id)
        });
    }
}
