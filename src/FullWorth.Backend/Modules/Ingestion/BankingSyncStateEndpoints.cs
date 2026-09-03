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
        return app;
    }
}
