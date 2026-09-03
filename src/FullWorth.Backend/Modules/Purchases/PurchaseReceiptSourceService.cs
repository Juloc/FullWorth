using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed class PurchaseReceiptSourceService(
    FullWorthDbContext db,
    PurchaseAuthorizationStore authorization,
    PurchaseSemanticDuplicateDetector duplicates)
{
    public async Task<object?> GetAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) == PurchaseAccessLevel.None)
            return null;

        var sources = await db.Database.SqlQuery<ReceiptSourceProjection>($"""
            SELECT s."Id", s."PurchaseDocumentId", s."SortOrder", s."SourceType", s."OriginalFileName",
                   s."MimeType", s."PageNumber", s."Fingerprint"
            FROM "ReceiptScanSources" s
            JOIN "ReceiptScanJobs" j ON j."Id" = s."ReceiptScanJobId"
            WHERE j."PurchaseId" = {purchaseId}
            ORDER BY s."SortOrder", s."Id"
            """).ToListAsync(ct);

        var itemSources = await db.Database.SqlQuery<ItemSourceProjection>($"""
            SELECT l."PurchaseItemId", l."ReceiptScanSourceId"
            FROM "ReceiptScanItemSources" l
            JOIN "PurchaseItems" i ON i."Id" = l."PurchaseItemId"
            WHERE i."PurchaseId" = {purchaseId}
            ORDER BY l."PurchaseItemId", l."ReceiptScanSourceId"
            """).ToListAsync(ct);

        var purchase = await db.Purchases.AsNoTracking()
            .Include(x => x.Items)
            .SingleAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        var request = new PurchaseExtractionRequest(
            purchase.Merchant,
            purchase.PurchaseDate,
            purchase.TotalAmount,
            purchase.Currency,
            purchase.Items.OrderBy(x => x.SortOrder).Select(x => new PurchaseItemWrite(
                x.CategoryId, x.Name, x.Brand, x.Sku, x.Asin, x.Quantity, x.UnitPrice, x.TotalPrice,
                x.Currency, x.Notes, RawName: x.RawName, QuantityUnit: x.QuantityUnit,
                DepositAmount: x.DepositAmount, LineType: x.LineType, SortOrder: x.SortOrder,
                OriginalUnitPrice: x.OriginalUnitPrice, DiscountAmount: x.DiscountAmount,
                DiscountLabel: x.DiscountLabel)).ToList(),
            SourceReference: purchase.SourceReference,
            Notes: purchase.Notes,
            ReceiptNumber: purchase.ReceiptNumber,
            AmountsAreCanonical: true);
        var duplicateWarnings = await duplicates.DetectWarningsAsync(userId, fullWorthSpaceId, purchaseId, request, ct);

        return new
        {
            sources = sources.Select((source, index) => new
            {
                source.Id,
                source.PurchaseDocumentId,
                source.SortOrder,
                source.SourceType,
                source.OriginalFileName,
                source.MimeType,
                source.PageNumber,
                displayNumber = index + 1,
                contentUrl = source.PurchaseDocumentId.HasValue
                    ? $"/api/purchases/{purchaseId:D}/documents/{source.PurchaseDocumentId.Value:D}/content?fullWorthSpaceId={fullWorthSpaceId:D}"
                    : null
            }).ToList(),
            itemSources,
            duplicateWarnings
        };
    }

    public sealed record ReceiptSourceProjection(
        Guid Id,
        Guid? PurchaseDocumentId,
        int SortOrder,
        string SourceType,
        string OriginalFileName,
        string MimeType,
        int? PageNumber,
        string Fingerprint);

    public sealed record ItemSourceProjection(Guid PurchaseItemId, Guid ReceiptScanSourceId);
}

public static class PurchaseReceiptSourceEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseReceiptSourceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/purchases/{purchaseId:guid}/receipt-sources", async (
            Guid purchaseId,
            Guid fullWorthSpaceId,
            FullWorth.Backend.Security.CurrentUserContext user,
            PurchaseReceiptSourceService service,
            CancellationToken ct) =>
        {
            var value = await service.GetAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, ct);
            return value is null ? Results.NotFound() : Results.Ok(value);
        }).WithTags("Purchases");
        return app;
    }
}
