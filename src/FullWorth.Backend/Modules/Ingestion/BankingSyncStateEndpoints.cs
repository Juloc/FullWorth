using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Ingestion;

public static class BankingSyncStateEndpoints
{
    public static IEndpointRouteBuilder MapBankingSyncStateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/internal/banking/connections/{connectionId:guid}/accounts/{identificationHash}/sync-state", async (
            Guid connectionId,
            string identificationHash,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var account = await db.Accounts.AsNoTracking()
                .Where(x => x.BankConnectionId == connectionId && x.IdentificationHash == identificationHash)
                .Select(x => new
                {
                    x.Id,
                    x.IdentificationHash,
                    LatestBookingDate = db.Transactions.Where(t => t.AccountId == x.Id && t.BookingDate != null)
                        .Max(t => (DateOnly?)t.BookingDate),
                    LatestTransactionUpdatedAt = db.Transactions.Where(t => t.AccountId == x.Id)
                        .Max(t => (DateTimeOffset?)t.UpdatedAt)
                })
                .SingleOrDefaultAsync(ct);
            return account is null ? Results.NotFound() : Results.Ok(account);
        }).WithTags("Internal banking");

        app.MapGet("/internal/banking/transactions/{transactionId:guid}/provider-pointer", async (
            HttpContext http,
            Guid transactionId,
            Guid fullWorthSpaceId,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.Request.Headers["X-FullWorth-User-Id"], out var userId) || userId == Guid.Empty)
                return Results.BadRequest();

            var pointer = await db.Transactions.AsNoTracking()
                .Where(t => t.Id == transactionId)
                .Join(db.Accounts.AsNoTracking(),
                    transaction => transaction.AccountId,
                    account => account.Id,
                    (transaction, account) => new { transaction, account })
                .Where(x =>
                    x.account.FullWorthSpaceId == fullWorthSpaceId &&
                    x.account.BankConnectionId != null &&
                    x.account.Owners.Any(owner => owner.UserId == userId) &&
                    db.FullWorthSpaceMembers.Any(member =>
                        member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId))
                .Select(x => new
                {
                    ConnectionId = x.account.BankConnectionId!.Value,
                    ProviderAccountId = x.account.ProviderAccountId,
                    x.transaction.ProviderTransactionId
                })
                .SingleOrDefaultAsync(ct);

            return pointer is null || string.IsNullOrWhiteSpace(pointer.ProviderAccountId)
                ? Results.NotFound()
                : Results.Ok(pointer);
        }).WithTags("Internal banking");

        return app;
    }
}
