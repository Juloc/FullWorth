using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Parity;

/// <summary>
/// Compatibility guard for legacy investment read endpoints that predate account-scoped portfolio reads.
/// New investment writes use the capability-aware v2/management endpoints; this middleware only closes
/// the legacy portfolio-list visibility gap without changing route contracts.
/// </summary>
public sealed class InvestmentLegacyReadSecurityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        CurrentUserContext currentUser,
        FullWorthDbContext db)
    {
        if (HttpMethods.IsGet(context.Request.Method) &&
            context.Request.Path.Equals("/api/investments/portfolios", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePortfolioListAsync(context, currentUser, db);
            return;
        }

        await next(context);
    }

    private static async Task HandlePortfolioListAsync(
        HttpContext context,
        CurrentUserContext currentUser,
        FullWorthDbContext db)
    {
        if (!Guid.TryParse(context.Request.Query["fullWorthSpaceId"], out var fullWorthSpaceId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var userId = currentUser.RequireUserId();
        var ct = context.RequestAborted;
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var visibleAccounts = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT "Id","Name","Currency","AccountId","BenchmarkSecurityId","IsArchived","CreatedAt","UpdatedAt"
FROM "InvestmentPortfolios"
WHERE "FullWorthSpaceId"=@space
ORDER BY "IsArchived","Name"
""", ("@space", fullWorthSpaceId));

        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            var accountId = ParitySql.NullableGuid(reader, "AccountId");
            if (accountId.HasValue && !visibleAccounts.Contains(accountId.Value))
                continue;

            rows.Add(new
            {
                id = ParitySql.Guid(reader, "Id"),
                name = ParitySql.String(reader, "Name"),
                currency = ParitySql.String(reader, "Currency"),
                accountId,
                benchmarkSecurityId = ParitySql.NullableGuid(reader, "BenchmarkSecurityId"),
                isArchived = ParitySql.Bool(reader, "IsArchived"),
                createdAt = ParitySql.Timestamp(reader, "CreatedAt"),
                updatedAt = ParitySql.Timestamp(reader, "UpdatedAt")
            });
        }

        await Results.Ok(rows).ExecuteAsync(context);
    }
}
