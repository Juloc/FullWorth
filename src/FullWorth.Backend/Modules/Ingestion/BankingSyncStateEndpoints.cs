using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Ingestion;

public static class BankingSyncStateEndpoints
{
    public static IEndpointRouteBuilder MapBankingSyncStateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/internal/banking/connections/{connectionId:guid}/accounts/sync-state", async (
            Guid connectionId,
            string identificationHash,
            FullWorthDbContext db,
            CancellationToken ct) =>
        {
            var candidates = await db.Accounts.AsNoTracking()
                .Where(x => x.BankConnectionId == connectionId)
                .Select(x => new { x.Id, x.IdentificationHash, x.IdentificationHashesJson })
                .ToListAsync(ct);

            // Only the provider's primary identification_hash is a stable account identity.
            // Fuzzy aliases from identification_hashes are explicitly not guaranteed to be unique.
            var matchingIds = candidates
                .Where(x => string.Equals(x.IdentificationHash, identificationHash, StringComparison.Ordinal))
                .Select(x => x.Id)
                .Distinct()
                .ToArray();

            if (matchingIds.Length == 0) return Results.NotFound();
            if (matchingIds.Length > 1)
                return Results.Conflict(new { error = "ambiguous_account_identification_hash" });

            var accountId = matchingIds[0];
            var primaryHash = candidates.Single(x => x.Id == accountId).IdentificationHash;
            var account = new
            {
                Id = accountId,
                IdentificationHash = primaryHash,
                LatestBookingDate = await db.Transactions.AsNoTracking()
                    .Where(t => t.AccountId == accountId && t.BookingDate != null && t.Status == "BOOK")
                    .MaxAsync(t => (DateOnly?)t.BookingDate, ct),
                LatestTransactionUpdatedAt = await db.Transactions.AsNoTracking()
                    .Where(t => t.AccountId == accountId)
                    .MaxAsync(t => (DateTimeOffset?)t.UpdatedAt, ct)
            };
            return Results.Ok(account);
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
