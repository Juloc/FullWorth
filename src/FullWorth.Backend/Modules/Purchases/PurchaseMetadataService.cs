using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record TagWrite(string Name);
public sealed record AttachTagWrite(Guid TagId);

public sealed class PurchaseMetadataService(FullWorthDbContext db)
{
    public async Task<object?> ListTagsAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        return await db.Set<FinanceTag>().AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, useCount = x.PurchaseLinks.Count() }).ToListAsync(ct);
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> CreateTagAsync(Guid userId, Guid fullWorthSpaceId, TagWrite request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return (PurchaseMutationResult.NotFound, null, null);
        var name = request.Name?.Trim(); if (string.IsNullOrWhiteSpace(name)) return (PurchaseMutationResult.Invalid, null, "Tag name is required.");
        var normalized = ProductService.Normalize(name);
        var existing = await db.Set<FinanceTag>().SingleOrDefaultAsync(x => x.FullWorthSpaceId == fullWorthSpaceId && x.NormalizedName == normalized, ct);
        if (existing is not null) return (PurchaseMutationResult.Success, new { existing.Id, existing.Name }, null);
        var tag = new FinanceTag { FullWorthSpaceId = fullWorthSpaceId, Name = name, NormalizedName = normalized };
        db.Add(tag); await db.SaveChangesAsync(ct);
        return (PurchaseMutationResult.Success, new { tag.Id, tag.Name }, null);
    }

    public async Task<PurchaseMutationResult> DeleteTagAsync(Guid userId, Guid fullWorthSpaceId, Guid tagId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return PurchaseMutationResult.NotFound;
        var tag = await db.Set<FinanceTag>().SingleOrDefaultAsync(x => x.Id == tagId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (tag is null) return PurchaseMutationResult.NotFound;
        db.Remove(tag); await db.SaveChangesAsync(ct); return PurchaseMutationResult.Success;
    }

    public async Task<object?> PurchaseTagsAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (!await Visible(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct)) return null;
        return await db.Set<PurchaseTagLink>().AsNoTracking().Where(x => x.PurchaseId == purchaseId)
            .OrderBy(x => x.Tag.Name).Select(x => new { x.Tag.Id, x.Tag.Name }).ToListAsync(ct);
    }

    public async Task<PurchaseMutationResult> AttachTagAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid tagId, CancellationToken ct)
    {
        var access = await AccessAsync(userId, fullWorthSpaceId, purchaseId, ct); if (access != PurchaseMutationResult.Success) return access;
        if (!await db.Set<FinanceTag>().AnyAsync(x => x.Id == tagId && x.FullWorthSpaceId == fullWorthSpaceId, ct)) return PurchaseMutationResult.NotFound;
        if (!await db.Set<PurchaseTagLink>().AnyAsync(x => x.PurchaseId == purchaseId && x.TagId == tagId, ct)) db.Add(new PurchaseTagLink { PurchaseId = purchaseId, TagId = tagId });
        await db.SaveChangesAsync(ct); return PurchaseMutationResult.Success;
    }

    public async Task<PurchaseMutationResult> DetachTagAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid tagId, CancellationToken ct)
    {
        var access = await AccessAsync(userId, fullWorthSpaceId, purchaseId, ct); if (access != PurchaseMutationResult.Success) return access;
        var link = await db.Set<PurchaseTagLink>().SingleOrDefaultAsync(x => x.PurchaseId == purchaseId && x.TagId == tagId, ct);
        if (link is null) return PurchaseMutationResult.NotFound;
        db.Remove(link); await db.SaveChangesAsync(ct); return PurchaseMutationResult.Success;
    }

    public async Task<object?> UpcomingWarrantyAsync(Guid userId, Guid fullWorthSpaceId, int days, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        days = Math.Clamp(days <= 0 ? 90 : days, 1, 3650);
        var today = DateOnly.FromDateTime(DateTime.UtcNow); var end = today.AddDays(days);
        var rows = await VisibleItems(userId, fullWorthSpaceId)
            .Where(x => (x.ReturnDeadline.HasValue && x.ReturnDeadline >= today && x.ReturnDeadline <= end) || (x.WarrantyEnd.HasValue && x.WarrantyEnd >= today && x.WarrantyEnd <= end))
            .OrderBy(x => x.ReturnDeadline ?? x.WarrantyEnd)
            .Select(x => new { x.Id, x.PurchaseId, x.Name, x.ProductId, x.SerialNumber, x.ReturnDeadline, x.WarrantyEnd, x.Purchase.Merchant, x.Purchase.PurchaseDate, x.TotalPrice, x.Currency })
            .ToListAsync(ct);
        return new { today, through = end, items = rows };
    }

    public async Task<object?> ItemReturnsAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid itemId, CancellationToken ct)
    {
        if (!await VisibleItems(userId, fullWorthSpaceId).AnyAsync(x => x.Id == itemId && x.PurchaseId == purchaseId, ct)) return null;
        return await db.Set<PurchaseItemReturn>().AsNoTracking().Where(x => x.PurchaseItemId == itemId).OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.RefundTransactionId, x.Quantity, x.Amount, x.Currency, x.Status, x.Note, x.CreatedAt }).ToListAsync(ct);
    }

    public async Task<PurchaseMutationResult> DeleteReturnAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid itemId, Guid returnId, CancellationToken ct)
    {
        var access = await AccessAsync(userId, fullWorthSpaceId, purchaseId, ct); if (access != PurchaseMutationResult.Success) return access;
        var row = await db.Set<PurchaseItemReturn>().SingleOrDefaultAsync(x => x.Id == returnId && x.PurchaseItemId == itemId && x.PurchaseItem.PurchaseId == purchaseId, ct);
        if (row is null) return PurchaseMutationResult.NotFound;
        if (row.RefundTransactionId.HasValue)
        {
            var refund = await db.Transactions.SingleOrDefaultAsync(x => x.Id == row.RefundTransactionId.Value, ct);
            if (refund is not null) { refund.RefundOfTransactionId = null; refund.RefundCategoryId = null; refund.UpdatedAt = DateTimeOffset.UtcNow; }
        }
        db.Remove(row); await db.SaveChangesAsync(ct); return PurchaseMutationResult.Success;
    }

    private IQueryable<Purchase> Visible(Guid userId, Guid fullWorthSpaceId) => db.Purchases.AsNoTracking().Where(p => p.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) && (p.Visibility != "private" || p.CreatedByUserId == userId) && (!p.PaymentLinks.Any() || p.PaymentLinks.Any(l => db.Transactions.Any(t => t.Id == l.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId))))) && (p.TransactionId == null || db.Transactions.Any(t => t.Id == p.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId)))));
    private IQueryable<Purchase> Writable(Guid userId, Guid fullWorthSpaceId) => db.Purchases.Where(p => p.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) && (p.Visibility != "private" || p.CreatedByUserId == userId) && (!p.PaymentLinks.Any() || p.PaymentLinks.All(l => db.Transactions.Any(t => t.Id == l.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId && o.OwnershipType == AccountOwnershipTypes.Owner))))) && (p.TransactionId == null || db.Transactions.Any(t => t.Id == p.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId && o.OwnershipType == AccountOwnershipTypes.Owner)))));
    private IQueryable<PurchaseItem> VisibleItems(Guid userId, Guid fullWorthSpaceId) => db.PurchaseItems.AsNoTracking().Where(i => i.Purchase.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) && (i.Purchase.Visibility != "private" || i.Purchase.CreatedByUserId == userId) && (!i.Purchase.PaymentLinks.Any() || i.Purchase.PaymentLinks.Any(l => db.Transactions.Any(t => t.Id == l.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId))))) && (i.Purchase.TransactionId == null || db.Transactions.Any(t => t.Id == i.Purchase.TransactionId && db.Accounts.Any(a => a.Id == t.AccountId && a.Owners.Any(o => o.UserId == userId)))));
    private async Task<PurchaseMutationResult> AccessAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct) { if (await Writable(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct)) return PurchaseMutationResult.Success; return await Visible(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct) ? PurchaseMutationResult.Forbidden : PurchaseMutationResult.NotFound; }
    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) => db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
}

public static class PurchaseMetadataEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        var tags = app.MapGroup("/api/tags").WithTags("Purchases");
        tags.MapGet("/", async (Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => { var v = await service.ListTagsAsync(user.RequireUserId(), fullWorthSpaceId, ct); return v is null ? Results.NotFound() : Results.Ok(v); });
        tags.MapPost("/", async (Guid fullWorthSpaceId, TagWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => Outcome(await service.CreateTagAsync(user.RequireUserId(), fullWorthSpaceId, request, ct), true));
        tags.MapDelete("/{tagId:guid}", async (Guid tagId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => Mutation(await service.DeleteTagAsync(user.RequireUserId(), fullWorthSpaceId, tagId, ct)));
        app.MapGet("/api/purchases/{id:guid}/tags", async (Guid id, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => { var v = await service.PurchaseTagsAsync(user.RequireUserId(), fullWorthSpaceId, id, ct); return v is null ? Results.NotFound() : Results.Ok(v); });
        app.MapPost("/api/purchases/{id:guid}/tags", async (Guid id, Guid fullWorthSpaceId, AttachTagWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => Mutation(await service.AttachTagAsync(user.RequireUserId(), fullWorthSpaceId, id, request.TagId, ct)));
        app.MapDelete("/api/purchases/{id:guid}/tags/{tagId:guid}", async (Guid id, Guid tagId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => Mutation(await service.DetachTagAsync(user.RequireUserId(), fullWorthSpaceId, id, tagId, ct)));
        app.MapGet("/api/purchases/warranty/upcoming", async (Guid fullWorthSpaceId, int? days, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => { var v = await service.UpcomingWarrantyAsync(user.RequireUserId(), fullWorthSpaceId, days ?? 90, ct); return v is null ? Results.NotFound() : Results.Ok(v); });
        app.MapGet("/api/purchases/{id:guid}/items/{itemId:guid}/returns", async (Guid id, Guid itemId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => { var v = await service.ItemReturnsAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, ct); return v is null ? Results.NotFound() : Results.Ok(v); });
        app.MapDelete("/api/purchases/{id:guid}/items/{itemId:guid}/returns/{returnId:guid}", async (Guid id, Guid itemId, Guid returnId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseMetadataService service, CancellationToken ct) => Mutation(await service.DeleteReturnAsync(user.RequireUserId(), fullWorthSpaceId, id, itemId, returnId, ct)));
        return app;
    }
    private static IResult Mutation(PurchaseMutationResult r) => r switch { PurchaseMutationResult.Success => Results.NoContent(), PurchaseMutationResult.Forbidden => Results.StatusCode(403), PurchaseMutationResult.Invalid => Results.BadRequest(), _ => Results.NotFound() };
    private static IResult Outcome((PurchaseMutationResult Result, object? Value, string? Error) o, bool created = false) => o.Result switch { PurchaseMutationResult.Success when created => Results.Created(string.Empty, o.Value), PurchaseMutationResult.Success => Results.Ok(o.Value), PurchaseMutationResult.Invalid => Results.BadRequest(new { error = o.Error }), PurchaseMutationResult.Forbidden => Results.StatusCode(403), _ => Results.NotFound() };
}
