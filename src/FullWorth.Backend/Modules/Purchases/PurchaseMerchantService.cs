using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Merchants;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record PurchaseMerchantAssignRequest(Guid? MerchantId);

public sealed class PurchaseMerchantService(FullWorthDbContext db)
{
    public async Task<(PurchaseMutationResult Result, object? Value)> ResolveAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var purchase = await Writable(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return (await Visible(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct) ? PurchaseMutationResult.Forbidden : PurchaseMutationResult.NotFound, null);
        var raw = purchase.MerchantRaw ?? purchase.Merchant;
        var normalized = MerchantNormalization.Normalize(raw);
        if (normalized is null) return (PurchaseMutationResult.Invalid, null);

        var direct = await db.Set<Merchant>().AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.NormalizedName == normalized)
            .Select(x => new { x.Id, x.Name }).SingleOrDefaultAsync(ct);
        Guid? merchantId = direct?.Id;
        string? merchantName = direct?.Name;
        if (!merchantId.HasValue)
        {
            var aliases = await db.Set<MerchantAlias>().AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
                .Select(x => new { x.MerchantId, x.NormalizedAlias }).ToListAsync(ct);
            var match = aliases.Where(x => normalized.Contains(x.NormalizedAlias, StringComparison.Ordinal)).OrderByDescending(x => x.NormalizedAlias.Length).FirstOrDefault();
            if (match is not null)
            {
                merchantId = match.MerchantId;
                merchantName = await db.Set<Merchant>().AsNoTracking().Where(x => x.Id == merchantId.Value).Select(x => x.Name).SingleAsync(ct);
            }
        }
        if (merchantId.HasValue)
        {
            purchase.MerchantRaw ??= purchase.Merchant;
            purchase.MerchantId = merchantId;
            purchase.Merchant = merchantName!;
            purchase.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return (PurchaseMutationResult.Success, new { normalized, merchantId, merchantName, matched = merchantId.HasValue });
    }

    public async Task<PurchaseMutationResult> AssignAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid? merchantId, CancellationToken ct)
    {
        var purchase = await Writable(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return await Visible(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct) ? PurchaseMutationResult.Forbidden : PurchaseMutationResult.NotFound;
        if (!merchantId.HasValue)
        {
            purchase.MerchantId = null;
            purchase.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return PurchaseMutationResult.Success;
        }
        var merchant = await db.Set<Merchant>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == merchantId.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (merchant is null) return PurchaseMutationResult.NotFound;
        purchase.MerchantRaw ??= purchase.Merchant;
        purchase.MerchantId = merchant.Id;
        purchase.Merchant = merchant.Name;
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    private IQueryable<Purchase> Visible(Guid userId, Guid fullWorthSpaceId) => db.Purchases.Where(p => p.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) && (p.Visibility != "private" || p.CreatedByUserId == userId) && (!p.PaymentLinks.Any() || p.PaymentLinks.Any(l => db.Transactions.Any(t => t.Id == l.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId))))) && (p.TransactionId == null || db.Transactions.Any(t => t.Id == p.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId)))));
    private IQueryable<Purchase> Writable(Guid userId, Guid fullWorthSpaceId) => db.Purchases.Where(p => p.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) && (p.Visibility != "private" || p.CreatedByUserId == userId) && (!p.PaymentLinks.Any() || p.PaymentLinks.All(l => db.Transactions.Any(t => t.Id == l.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId && o.OwnershipType == AccountOwnershipTypes.Owner))))) && (p.TransactionId == null || db.Transactions.Any(t => t.Id == p.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId && o.OwnershipType == AccountOwnershipTypes.Owner)))));
}

public static class PurchaseMerchantEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseMerchantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases/{id:guid}/merchant").WithTags("Purchases");
        group.MapPost("/resolve", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMerchantService service, CancellationToken ct) =>
        {
            var outcome = await service.ResolveAsync(user.RequireUserId(), fullWorthSpaceId, id, ct);
            return outcome.Result switch { PurchaseMutationResult.Success => Results.Ok(outcome.Value), PurchaseMutationResult.Forbidden => Results.StatusCode(403), PurchaseMutationResult.Invalid => Results.BadRequest(), _ => Results.NotFound() };
        });
        group.MapPut("/", async (Guid id, Guid fullWorthSpaceId, PurchaseMerchantAssignRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMerchantService service, CancellationToken ct) =>
        {
            var result = await service.AssignAsync(user.RequireUserId(), fullWorthSpaceId, id, request.MerchantId, ct);
            return result switch { PurchaseMutationResult.Success => Results.NoContent(), PurchaseMutationResult.Forbidden => Results.StatusCode(403), PurchaseMutationResult.Invalid => Results.BadRequest(), _ => Results.NotFound() };
        });
        return app;
    }
}
