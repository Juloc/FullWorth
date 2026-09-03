using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record ProductIdentityWrite(
    string CanonicalName, string? Brand, string? Barcode, Guid? DefaultCategoryId,
    string? UnitKind, decimal? UnitSize);
public sealed record ProductIdentityAliasWrite(string Text, decimal? Confidence = 1m, string Source = "manual");
public sealed record ProductItemLinkWrite(Guid ProductIdentityId, decimal? Confidence = 1m, string Source = "manual");

/// <summary>
/// Compatibility facade for the parity UI. ProductIdentities was a temporary parallel model; all reads
/// and writes now target the canonical Products/ProductAliases/ProductBarcodes/PurchaseItem.ProductId
/// model so there is one product identity throughout FullWorth.
/// </summary>
public static class ProductIdentityParityEndpoints
{
    private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase)
    { "piece", "g", "kg", "ml", "l", "m", "cm", "wash_load", "tablet" };

    public static IEndpointRouteBuilder MapProductIdentityParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/product-identities").WithTags("Purchases");
        group.MapGet("/", List);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Archive);
        group.MapPost("/{id:guid}/aliases", AddAlias);
        group.MapDelete("/{id:guid}/aliases/{aliasId:guid}", DeleteAlias);
        group.MapGet("/{id:guid}/history", History);
        group.MapGet("/suggest", Suggest);
        group.MapPut("/purchase-items/{purchaseItemId:guid}", LinkItem);
        group.MapDelete("/purchase-items/{purchaseItemId:guid}", UnlinkItem);
        return app;
    }

    private static async Task<IResult> List(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var rows = await db.Products.AsNoTracking()
            .Where(p => p.FullWorthSpaceId == fullWorthSpaceId && !p.IsArchived)
            .OrderBy(p => p.CanonicalName)
            .Select(p => new
            {
                id = p.Id,
                canonicalName = p.CanonicalName,
                brand = p.Brand,
                barcode = p.Barcodes.OrderBy(b => b.CreatedAt).Select(b => b.Code).FirstOrDefault(),
                defaultCategoryId = p.DefaultCategoryId,
                unitKind = p.DefaultPackageUnit ?? p.DefaultQuantityUnit,
                unitSize = p.DefaultPackageQuantity,
                aliasCount = p.Aliases.Count
            }).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static Task<IResult> Create(
        Guid fullWorthSpaceId, ProductIdentityWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
        Write(Guid.NewGuid(), fullWorthSpaceId, request, currentUser, db, audit, false, ct);

    private static Task<IResult> Update(
        Guid id, Guid fullWorthSpaceId, ProductIdentityWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct) =>
        Write(id, fullWorthSpaceId, request, currentUser, db, audit, true, ct);

    private static async Task<IResult> Write(
        Guid id, Guid fullWorthSpaceId, ProductIdentityWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, bool update, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.CanonicalName)) return Results.BadRequest(new { error = "Product name is required." });
        var unit = NormalizeUnit(request.UnitKind);
        if (unit is not null && !Units.Contains(unit)) return Results.BadRequest(new { error = "Unsupported product unit." });
        if (request.UnitSize is <= 0) return Results.BadRequest(new { error = "Unit size must be positive." });
        if (request.DefaultCategoryId.HasValue && !await db.Categories.AsNoTracking().AnyAsync(c =>
                c.Id == request.DefaultCategoryId.Value && c.FullWorthSpaceId == fullWorthSpaceId && !c.IsArchived, ct))
            return Results.BadRequest(new { error = "Category is invalid." });

        var barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode.Trim();
        if (barcode is not null && await db.ProductBarcodes.AsNoTracking().AnyAsync(b => b.Code == barcode && b.ProductId != id, ct))
            return Results.Conflict(new { error = "This barcode is already linked to another product." });

        Product product;
        if (update)
        {
            product = await db.Products.Include(p => p.Barcodes)
                .SingleOrDefaultAsync(p => p.Id == id && p.FullWorthSpaceId == fullWorthSpaceId, ct)
                ?? null!;
            if (product is null) return Results.NotFound();
        }
        else
        {
            product = new Product { Id = id, FullWorthSpaceId = fullWorthSpaceId, CreatedAt = DateTimeOffset.UtcNow };
            db.Products.Add(product);
        }

        product.CanonicalName = request.CanonicalName.Trim();
        product.Brand = Clean(request.Brand);
        product.DefaultCategoryId = request.DefaultCategoryId;
        product.DefaultQuantityUnit = "piece";
        product.DefaultPackageQuantity = request.UnitSize;
        product.DefaultPackageUnit = unit;
        product.IsArchived = false;
        product.UpdatedAt = DateTimeOffset.UtcNow;

        var existingBarcodes = update ? product.Barcodes.ToList() : [];
        if (barcode is null)
        {
            if (existingBarcodes.Count > 0) db.ProductBarcodes.RemoveRange(existingBarcodes);
        }
        else
        {
            var first = existingBarcodes.FirstOrDefault();
            if (first is null)
                db.ProductBarcodes.Add(new ProductBarcode { ProductId = id, Code = barcode, Standard = GuessBarcodeStandard(barcode) });
            else
            {
                first.Code = barcode;
                first.Standard = GuessBarcodeStandard(barcode);
                if (existingBarcodes.Count > 1) db.ProductBarcodes.RemoveRange(existingBarcodes.Skip(1));
            }
        }

        audit.Record(fullWorthSpaceId, userId, update ? "product.updated" : "product.created", "Product", id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id });
    }

    private static async Task<IResult> Archive(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id && p.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (product is null) return Results.NotFound();
        product.IsArchived = true;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "product.archived", "Product", id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AddAlias(
        Guid id, Guid fullWorthSpaceId, ProductIdentityAliasWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await ProductExists(db, fullWorthSpaceId, id, ct)) return Results.NotFound();
        var alias = Clean(request.Text);
        if (alias is null) return Results.BadRequest(new { error = "Alias is required." });
        var normalized = Normalize(alias);
        if (normalized.Length < 2) return Results.BadRequest(new { error = "Alias is too short." });

        var existing = await db.ProductAliases.SingleOrDefaultAsync(a =>
            a.ProductId == id && a.MerchantId == null && a.NormalizedAlias == normalized, ct);
        if (existing is null)
            db.ProductAliases.Add(new ProductAlias
            {
                ProductId = id, Alias = alias, NormalizedAlias = normalized,
                AliasType = NormalizeSource(request.Source), CreatedAt = DateTimeOffset.UtcNow
            });
        else
        {
            existing.Alias = alias;
            existing.AliasType = NormalizeSource(request.Source);
        }
        audit.Record(fullWorthSpaceId, userId, "product.alias.updated", "Product", id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAlias(
        Guid id, Guid aliasId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var alias = await db.ProductAliases.Include(a => a.Product)
            .SingleOrDefaultAsync(a => a.Id == aliasId && a.ProductId == id && a.Product.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (alias is null) return Results.NotFound();
        db.ProductAliases.Remove(alias);
        audit.Record(fullWorthSpaceId, userId, "product.alias.deleted", "Product", id);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Suggest(
        Guid fullWorthSpaceId, string text, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var normalized = Normalize(text);
        if (normalized.Length < 2) return Results.Ok(null);

        var alias = await db.ProductAliases.AsNoTracking()
            .Where(a => a.NormalizedAlias == normalized && a.Product.FullWorthSpaceId == fullWorthSpaceId && !a.Product.IsArchived)
            .Select(a => new
            {
                id = a.Product.Id, canonicalName = a.Product.CanonicalName, brand = a.Product.Brand,
                defaultCategoryId = a.Product.DefaultCategoryId, confidence = (decimal?)1m, source = a.AliasType
            }).FirstOrDefaultAsync(ct);
        if (alias is not null) return Results.Ok(alias);

        var products = await db.Products.AsNoTracking()
            .Where(p => p.FullWorthSpaceId == fullWorthSpaceId && !p.IsArchived)
            .Select(p => new { p.Id, p.CanonicalName, p.Brand, p.DefaultCategoryId })
            .Take(2000).ToListAsync(ct);
        var product = products.FirstOrDefault(p => Normalize(p.CanonicalName) == normalized);
        return product is null
            ? Results.Ok(null)
            : Results.Ok(new { id = product.Id, canonicalName = product.CanonicalName, brand = product.Brand, defaultCategoryId = product.DefaultCategoryId, confidence = (decimal?)1m, source = "canonical_name" });
    }

    private static async Task<IResult> History(
        Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct) || !await ProductExists(db, fullWorthSpaceId, id, ct))
            return Results.NotFound();
        var rows = await db.PurchaseItems.AsNoTracking()
            .Where(i => i.ProductId == id && i.Purchase.FullWorthSpaceId == fullWorthSpaceId &&
                (i.Purchase.Visibility != "private" || i.Purchase.CreatedByUserId == userId) &&
                (i.Purchase.ReviewState == "confirmed" || i.Purchase.Status == "confirmed"))
            .OrderByDescending(i => i.Purchase.PurchaseDate).ThenByDescending(i => i.CreatedAt)
            .Select(i => new
            {
                purchaseItemId = i.Id,
                purchaseDate = i.Purchase.PurchaseDate,
                merchant = i.Purchase.Merchant,
                quantity = i.Quantity,
                packageQuantity = i.PackageQuantity,
                packageUnit = i.PackageUnit,
                total = i.TotalPrice,
                currency = i.Currency,
                comparableUnitPrice = i.BaseUnitPrice,
                comparisonSafe = i.BaseUnitPrice != null && i.PackageQuantity != null && i.PackageUnit != null
            }).Take(500).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> LinkItem(
        Guid purchaseItemId, Guid fullWorthSpaceId, ProductItemLinkWrite request, CurrentUserContext currentUser,
        FullWorthDbContext db, PurchaseAuthorizationStore purchases, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await ProductExists(db, fullWorthSpaceId, request.ProductIdentityId, ct)) return Results.BadRequest(new { error = "Product is invalid." });
        var item = await db.PurchaseItems.SingleOrDefaultAsync(i => i.Id == purchaseItemId, ct);
        if (item is null || await purchases.GetAccessAsync(userId, fullWorthSpaceId, item.PurchaseId, ct) != PurchaseAccessLevel.Write)
            return Results.NotFound();
        item.ProductId = request.ProductIdentityId;
        item.IsManuallyCorrected = true;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "purchase.item.product.linked", "PurchaseItem", purchaseItemId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UnlinkItem(
        Guid purchaseItemId, Guid fullWorthSpaceId, CurrentUserContext currentUser,
        FullWorthDbContext db, PurchaseAuthorizationStore purchases, AuditService audit, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var item = await db.PurchaseItems.SingleOrDefaultAsync(i => i.Id == purchaseItemId, ct);
        if (item is null || await purchases.GetAccessAsync(userId, fullWorthSpaceId, item.PurchaseId, ct) != PurchaseAccessLevel.Write)
            return Results.NotFound();
        item.ProductId = null;
        item.IsManuallyCorrected = true;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(fullWorthSpaceId, userId, "purchase.item.product.unlinked", "PurchaseItem", purchaseItemId);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static Task<bool> ProductExists(FullWorthDbContext db, Guid fullWorthSpaceId, Guid id, CancellationToken ct) =>
        db.Products.AsNoTracking().AnyAsync(p => p.Id == id && p.FullWorthSpaceId == fullWorthSpaceId && !p.IsArchived, ct);

    private static string Normalize(string value) => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeUnit(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    private static string NormalizeSource(string? value) => string.IsNullOrWhiteSpace(value) ? "manual" : value.Trim().ToLowerInvariant()[..Math.Min(32, value.Trim().Length)];
    private static string GuessBarcodeStandard(string code) => code.Length switch { 8 => "ean8", 12 => "upc", 13 => "ean13", 14 => "gtin14", _ => "unknown" };
}
