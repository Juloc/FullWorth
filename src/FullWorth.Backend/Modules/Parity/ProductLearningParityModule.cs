using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed record ProductCategoryLearningAccept(string Text, Guid CategoryId, Guid? ProductIdentityId = null, string? CanonicalName = null);

/// <summary>Compatibility learning endpoints backed exclusively by the canonical product model.</summary>
public static class ProductLearningParityEndpoints
{
    public static IEndpointRouteBuilder MapProductLearningParityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/product-learning").WithTags("Purchases");
        group.MapGet("/category-suggestions", Suggestions);
        group.MapPost("/category-suggestions/accept", AcceptSuggestion);
        group.MapGet("/products/{productId:guid}/aliases", Aliases);
        return app;
    }

    private static async Task<IResult> Suggestions(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();

        var rows = await db.PurchaseItems.AsNoTracking()
            .Where(i => i.CategoryId.HasValue && i.CategorizationSource == "manual" &&
                i.Purchase.FullWorthSpaceId == fullWorthSpaceId &&
                (i.Purchase.Visibility != "private" || i.Purchase.CreatedByUserId == userId))
            .Select(i => new { i.Name, CategoryId = i.CategoryId!.Value })
            .Take(20000).ToListAsync(ct);

        var candidates = rows
            .Select(row => new { row.Name, Normalized = Normalize(row.Name), row.CategoryId })
            .Where(row => row.Normalized.Length >= 2)
            .GroupBy(row => row.Normalized)
            .Select(group =>
            {
                var byCategory = group.GroupBy(row => row.CategoryId)
                    .Select(category => new { CategoryId = category.Key, Count = category.Count() })
                    .OrderByDescending(category => category.Count).ThenBy(category => category.CategoryId).ToArray();
                var best = byCategory[0];
                return new
                {
                    Normalized = group.Key,
                    DisplayName = group.GroupBy(row => row.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(names => names.Count()).ThenBy(names => names.Key).First().Key,
                    best.CategoryId,
                    best.Count,
                    Total = group.Count(),
                    UniqueWinner = byCategory.Length == 1 || best.Count > byCategory[1].Count
                };
            })
            .Where(candidate => candidate.UniqueWinner && candidate.Count >= 3)
            .OrderByDescending(candidate => candidate.Count).ThenBy(candidate => candidate.DisplayName)
            .Take(200).ToArray();

        if (candidates.Length == 0) return Results.Ok(Array.Empty<object>());
        var categoryIds = candidates.Select(c => c.CategoryId).Distinct().ToArray();
        var categoryNames = await db.Categories.AsNoTracking().Where(c => c.FullWorthSpaceId == fullWorthSpaceId && categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var normalizedTexts = candidates.Select(c => c.Normalized).ToHashSet(StringComparer.Ordinal);
        var aliases = await db.ProductAliases.AsNoTracking()
            .Where(a => a.Product.FullWorthSpaceId == fullWorthSpaceId && normalizedTexts.Contains(a.NormalizedAlias))
            .Select(a => new { a.NormalizedAlias, ProductId = a.ProductId, ProductName = a.Product.CanonicalName, a.Product.DefaultCategoryId })
            .ToListAsync(ct);
        var byAlias = aliases.GroupBy(a => a.NormalizedAlias).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return Results.Ok(candidates
            .Where(candidate => !byAlias.TryGetValue(candidate.Normalized, out var alias) || alias.DefaultCategoryId != candidate.CategoryId)
            .Select(candidate =>
            {
                byAlias.TryGetValue(candidate.Normalized, out var alias);
                return new
                {
                    text = candidate.DisplayName,
                    normalizedText = candidate.Normalized,
                    categoryId = candidate.CategoryId,
                    category = categoryNames.GetValueOrDefault(candidate.CategoryId, string.Empty),
                    count = candidate.Count,
                    totalOccurrences = candidate.Total,
                    productIdentityId = alias?.ProductId,
                    productName = alias?.ProductName,
                    currentDefaultCategoryId = alias?.DefaultCategoryId
                };
            }));
    }

    private static async Task<IResult> AcceptSuggestion(
        Guid fullWorthSpaceId, ProductCategoryLearningAccept request, CurrentUserContext currentUser,
        FullWorthDbContext db, AuditService audit, IntelligenceFeedbackRecorder intelligenceFeedback, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(db, userId, fullWorthSpaceId, "purchases.manage", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (string.IsNullOrWhiteSpace(request.Text)) return Results.BadRequest(new { error = "Product text is required." });
        var targetCategory = await db.Categories.AsNoTracking()
            .Where(c => c.Id == request.CategoryId && c.FullWorthSpaceId == fullWorthSpaceId && !c.IsArchived)
            .Select(c => new { c.Key, c.IsSystem })
            .SingleOrDefaultAsync(ct);
        if (targetCategory is null) return Results.BadRequest(new { error = "Category is invalid." });
        var normalized = Normalize(request.Text);
        if (normalized.Length < 2) return Results.BadRequest(new { error = "Product text is too short." });

        Product? product = null;
        if (request.ProductIdentityId.HasValue)
            product = await db.Products.Include(p => p.Aliases).SingleOrDefaultAsync(p =>
                p.Id == request.ProductIdentityId.Value && p.FullWorthSpaceId == fullWorthSpaceId && !p.IsArchived, ct);
        else
            product = await db.ProductAliases.Where(a => a.NormalizedAlias == normalized && a.Product.FullWorthSpaceId == fullWorthSpaceId && !a.Product.IsArchived)
                .Select(a => a.Product).FirstOrDefaultAsync(ct);

        if (request.ProductIdentityId.HasValue && product is null) return Results.BadRequest(new { error = "Product is invalid." });
        var oldCategoryId = product?.DefaultCategoryId;
        if (product is null)
        {
            product = new Product
            {
                FullWorthSpaceId = fullWorthSpaceId,
                CanonicalName = string.IsNullOrWhiteSpace(request.CanonicalName) ? request.Text.Trim() : request.CanonicalName.Trim(),
                DefaultCategoryId = request.CategoryId,
                DefaultQuantityUnit = "piece",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Products.Add(product);
        }
        else
        {
            product.DefaultCategoryId = request.CategoryId;
            product.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        var alias = await db.ProductAliases.SingleOrDefaultAsync(a =>
            a.ProductId == product.Id && a.MerchantId == null && a.NormalizedAlias == normalized, ct);
        if (alias is null)
            db.ProductAliases.Add(new ProductAlias
            {
                ProductId = product.Id,
                Alias = request.Text.Trim(),
                NormalizedAlias = normalized,
                AliasType = "learning",
                CreatedAt = DateTimeOffset.UtcNow
            });
        else
        {
            alias.Alias = request.Text.Trim();
            alias.AliasType = "learning";
        }

        audit.Record(fullWorthSpaceId, userId, "product.category.learned", "Product", product.Id);
        await db.SaveChangesAsync(ct);

        string? publicProductKey = null;
        if (targetCategory.IsSystem)
        {
            var barcodes = await db.ProductBarcodes.AsNoTracking()
                .Where(barcode => barcode.ProductId == product.Id)
                .OrderBy(barcode => barcode.Code)
                .Select(barcode => barcode.Code)
                .ToListAsync(ct);
            foreach (var barcode in barcodes)
            {
                if (!CloudSubmissionProjector.TryCreateGtinSubjectKey(barcode, out var key)) continue;
                publicProductKey = key;
                break;
            }
        }

        await intelligenceFeedback.RecordProductCategoryAsync(
            fullWorthSpaceId,
            userId,
            product.Id,
            normalized,
            oldCategoryId,
            request.CategoryId,
            ct,
            publicProductKey,
            targetCategory.IsSystem ? targetCategory.Key : null);
        return Results.Ok(new { productIdentityId = product.Id, normalizedText = normalized, categoryId = request.CategoryId });
    }

    private static async Task<IResult> Aliases(
        Guid productId, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct) ||
            !await db.Products.AsNoTracking().AnyAsync(p => p.Id == productId && p.FullWorthSpaceId == fullWorthSpaceId && !p.IsArchived, ct))
            return Results.NotFound();
        var rows = await db.ProductAliases.AsNoTracking().Where(a => a.ProductId == productId)
            .OrderBy(a => a.NormalizedAlias)
            .Select(a => new
            {
                id = a.Id,
                text = a.Alias,
                normalizedText = a.NormalizedAlias,
                confidence = (decimal?)1m,
                source = a.AliasType,
                createdAt = a.CreatedAt
            }).ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static string Normalize(string value) => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
