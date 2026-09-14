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
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var visibleAccounts = await RawSql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var connection = await RawSql.OpenAsync(db, ct);
        await using var command = RawSql.Command(connection, """
SELECT "Id","Name","Currency","AccountId","BenchmarkSecurityId","IsArchived","CreatedAt","UpdatedAt"
FROM "InvestmentPortfolios"
WHERE "FullWorthSpaceId"=@space
ORDER BY "IsArchived","Name"
""", ("@space", fullWorthSpaceId));

        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            var accountId = RawSql.NullableGuid(reader, "AccountId");
            if (accountId.HasValue && !visibleAccounts.Contains(accountId.Value))
                continue;

            rows.Add(new
            {
                id = RawSql.Guid(reader, "Id"),
                name = RawSql.String(reader, "Name"),
                currency = RawSql.String(reader, "Currency"),
                accountId,
                benchmarkSecurityId = RawSql.NullableGuid(reader, "BenchmarkSecurityId"),
                isArchived = RawSql.Bool(reader, "IsArchived"),
                createdAt = RawSql.Timestamp(reader, "CreatedAt"),
                updatedAt = RawSql.Timestamp(reader, "UpdatedAt")
            });
        }

        await Results.Ok(rows).ExecuteAsync(context);
    }
}
