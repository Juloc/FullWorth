using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Portfolio;

public static class InvestmentNetWorthEndpoints
{
    public static IEndpointRouteBuilder MapInvestmentNetWorthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/investments/net-worth-contribution", GetContribution)
            .WithTags("Investments");
        return app;
    }

    private static async Task<IResult> GetContribution(
        Guid fullWorthSpaceId,
        DateOnly? asOf,
        CurrentUserContext currentUser,
        SpaceAccess access,
        InvestmentNetWorthService service,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

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
