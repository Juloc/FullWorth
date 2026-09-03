using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record PurchaseDiscountMutationWrite(
    Guid? PurchaseItemId,
    string Type,
    string? Label,
    decimal Amount,
    decimal? Percentage = null,
    string? CouponCode = null,
    string? RawText = null);

public sealed record PurchaseDiscountImport(
    Guid? PurchaseItemId,
    string Type,
    string? Label,
    decimal Amount,
    decimal? Percentage,
    string? CouponCode,
    string? RawText,
    string Source,
    decimal? Confidence,
    int? ItemIndex = null);

public static class PurchaseDiscountTypeCatalog
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "price_reduction", "percentage", "coupon", "loyalty", "multibuy", "bundle",
        "employee", "promotion", "other"
    };
}

/// <summary>
/// Canonical discount writer. Purchase.DiscountAmount and PurchaseItem discount fields are mirrors for
/// convenient review/export; PurchaseDiscount rows are the source of truth so basket promotions are
/// never forced onto arbitrary products.
/// </summary>
public sealed class PurchaseDiscountService(FullWorthDbContext db, PurchaseAuthorizationStore authorization)
{
    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> ListAsync(
        Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var access = await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return (PurchaseMutationResult.NotFound, null, null);
        var rows = await db.Set<PurchaseDiscount>().AsNoTracking()
            .Where(x => x.PurchaseId == purchaseId)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new
            {
                x.Id, x.PurchaseId, x.PurchaseItemId, x.Type, x.Label, x.Amount, x.Percentage,
                x.CouponCode, x.RawText, x.Source, x.Confidence, x.CreatedAt, x.UpdatedAt
            })
            .ToListAsync(ct);
        return (PurchaseMutationResult.Success, rows, null);
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> CreateAsync(
        Guid userId, Guid fullWorthSpaceId, Guid purchaseId, PurchaseDiscountMutationWrite request, CancellationToken ct)
    {
        var access = await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return (PurchaseMutationResult.NotFound, null, null);
        if (access != PurchaseAccessLevel.Write) return (PurchaseMutationResult.Forbidden, null, null);
        var validation = await ValidateAsync(fullWorthSpaceId, purchaseId, request.PurchaseItemId, request.Type, request.Amount, request.Percentage, ct);
        if (validation is not null) return (PurchaseMutationResult.Invalid, null, validation);

        var row = new PurchaseDiscount
        {
            PurchaseId = purchaseId,
            PurchaseItemId = request.PurchaseItemId,
            Type = NormalizeType(request.Type),
            Label = Cap(Clean(request.Label) ?? DefaultLabel(request.Type), 250),
            Amount = request.Amount,
            Percentage = request.Percentage,
            CouponCode = CapNullable(Clean(request.CouponCode), 120),
            RawText = CapNullable(Clean(request.RawText), 1000),
            Source = "manual",
            Confidence = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        await SyncMirrorsAndReviewAsync(purchaseId, ct);
        return (PurchaseMutationResult.Success, Dto(row), null);
    }

    public async Task<(PurchaseMutationResult Result, object? Value, string? Error)> UpdateAsync(
        Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid discountId, PurchaseDiscountMutationWrite request, CancellationToken ct)
    {
        var access = await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return (PurchaseMutationResult.NotFound, null, null);
        if (access != PurchaseAccessLevel.Write) return (PurchaseMutationResult.Forbidden, null, null);
        var row = await db.Set<PurchaseDiscount>().SingleOrDefaultAsync(x => x.Id == discountId && x.PurchaseId == purchaseId, ct);
        if (row is null) return (PurchaseMutationResult.NotFound, null, null);
        var validation = await ValidateAsync(fullWorthSpaceId, purchaseId, request.PurchaseItemId, request.Type, request.Amount, request.Percentage, ct);
        if (validation is not null) return (PurchaseMutationResult.Invalid, null, validation);

        row.PurchaseItemId = request.PurchaseItemId;
        row.Type = NormalizeType(request.Type);
        row.Label = Cap(Clean(request.Label) ?? DefaultLabel(request.Type), 250);
        row.Amount = request.Amount;
        row.Percentage = request.Percentage;
        row.CouponCode = CapNullable(Clean(request.CouponCode), 120);
        row.RawText = CapNullable(Clean(request.RawText), 1000);
        row.Source = "manual";
        row.Confidence = null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await SyncMirrorsAndReviewAsync(purchaseId, ct);
        return (PurchaseMutationResult.Success, Dto(row), null);
    }

    public async Task<PurchaseMutationResult> DeleteAsync(
        Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid discountId, CancellationToken ct)
    {
        var access = await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return PurchaseMutationResult.NotFound;
        if (access != PurchaseAccessLevel.Write) return PurchaseMutationResult.Forbidden;
        var row = await db.Set<PurchaseDiscount>().SingleOrDefaultAsync(x => x.Id == discountId && x.PurchaseId == purchaseId, ct);
        if (row is null) return PurchaseMutationResult.NotFound;
        db.Remove(row);
        await db.SaveChangesAsync(ct);
        await SyncMirrorsAndReviewAsync(purchaseId, ct);
        return PurchaseMutationResult.Success;
    }

    /// <summary>
    /// Replace machine/import discounts from exactly one source while preserving manual corrections and
    /// discounts imported by other providers. ItemIndex is resolved only after the new item set was persisted.
    /// An identical re-import is a true no-op: it must not reset review state or invalidate allocations.
    /// </summary>
    public async Task ReplaceSourceDiscountsAsync(
        Guid fullWorthSpaceId,
        Guid purchaseId,
        string source,
        IReadOnlyList<PurchaseDiscountImport> discounts,
        CancellationToken ct)
    {
        var normalizedSource = NormalizeSource(source);
        if (!await db.Purchases.AnyAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct))
            throw new InvalidOperationException("Purchase not found in FullWorth Space.");

        var itemIds = await db.PurchaseItems.AsNoTracking()
            .Where(x => x.PurchaseId == purchaseId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var desired = new List<NormalizedImport>();
        foreach (var input in discounts.Where(x => x.Amount > 0m))
        {
            Guid? itemId = input.PurchaseItemId;
            if (!itemId.HasValue && input.ItemIndex.HasValue)
            {
                if (input.ItemIndex.Value < 0 || input.ItemIndex.Value >= itemIds.Count)
                    throw new InvalidOperationException("Discount item index is outside the persisted purchase item range.");
                itemId = itemIds[input.ItemIndex.Value];
            }

            var validation = await ValidateAsync(fullWorthSpaceId, purchaseId, itemId, input.Type, input.Amount, input.Percentage, ct);
            if (validation is not null) throw new InvalidOperationException(validation);
            desired.Add(new NormalizedImport(
                itemId,
                NormalizeType(input.Type),
                Cap(Clean(input.Label) ?? DefaultLabel(input.Type), 250),
                input.Amount,
                input.Percentage,
                CapNullable(Clean(input.CouponCode), 120),
                CapNullable(Clean(input.RawText), 1000),
                input.Confidence.HasValue ? Math.Clamp(input.Confidence.Value, 0m, 1m) : null));
        }

        var current = await db.Set<PurchaseDiscount>()
            .Where(x => x.PurchaseId == purchaseId && x.Source == normalizedSource)
            .ToListAsync(ct);
        if (Equivalent(current, desired)) return;

        db.RemoveRange(current);
        var now = DateTimeOffset.UtcNow;
        foreach (var input in desired)
        {
            db.Add(new PurchaseDiscount
            {
                PurchaseId = purchaseId,
                PurchaseItemId = input.PurchaseItemId,
                Type = input.Type,
                Label = input.Label,
                Amount = input.Amount,
                Percentage = input.Percentage,
                CouponCode = input.CouponCode,
                RawText = input.RawText,
                Source = normalizedSource,
                Confidence = input.Confidence,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        await db.SaveChangesAsync(ct);
        await SyncMirrorsAndReviewAsync(purchaseId, ct);
    }

    public async Task SyncMirrorsAndReviewAsync(Guid purchaseId, CancellationToken ct)
    {
        var purchase = await db.Purchases.Include(x => x.Items).SingleAsync(x => x.Id == purchaseId, ct);
        var discounts = await db.Set<PurchaseDiscount>().AsNoTracking().Where(x => x.PurchaseId == purchaseId).ToListAsync(ct);
        purchase.DiscountAmount = PurchaseArticleCalculator.RoundMoney(discounts.Sum(x => x.Amount), purchase.Currency);

        foreach (var item in purchase.Items)
        {
            var assigned = discounts.Where(x => x.PurchaseItemId == item.Id).ToList();
            item.DiscountAmount = PurchaseArticleCalculator.RoundMoney(assigned.Sum(x => x.Amount), purchase.Currency);
            var labels = assigned.Select(x => Clean(x.Label)).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            item.DiscountLabel = labels.Count == 0 ? null : Cap(string.Join(" + ", labels), 250);
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (purchase.Status == "confirmed" || purchase.ReviewState == "confirmed")
        {
            purchase.Status = "review";
            purchase.ReviewState = "needs_review";
        }
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        var accepted = await db.Set<PurchaseDifferenceAcceptance>().Where(x => x.PurchaseId == purchaseId).ToListAsync(ct);
        db.RemoveRange(accepted);

        var generatedAllocationIds = await db.Set<PurchaseAllocationLink>()
            .Where(x => x.PurchaseId == purchaseId)
            .Select(x => x.TransactionAllocationId)
            .ToListAsync(ct);
        if (generatedAllocationIds.Count > 0)
        {
            var generatedAllocations = await db.TransactionAllocations
                .Where(x => generatedAllocationIds.Contains(x.Id))
                .ToListAsync(ct);
            if (generatedAllocations.Count > 0) db.TransactionAllocations.RemoveRange(generatedAllocations);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> ValidateAsync(
        Guid fullWorthSpaceId,
        Guid purchaseId,
        Guid? purchaseItemId,
        string type,
        decimal amount,
        decimal? percentage,
        CancellationToken ct)
    {
        if (!PurchaseDiscountTypeCatalog.Allowed.Contains(NormalizeType(type))) return "Discount type is invalid.";
        if (amount < 0m) return "Discount amount must be a positive saved amount.";
        if (percentage is < 0m or > 100m) return "Discount percentage must be between 0 and 100.";
        if (purchaseItemId.HasValue && !await db.PurchaseItems.AsNoTracking().AnyAsync(x =>
                x.Id == purchaseItemId.Value && x.PurchaseId == purchaseId && x.Purchase.FullWorthSpaceId == fullWorthSpaceId, ct))
            return "Discount item must belong to the same purchase and FullWorth Space.";
        return null;
    }

    private static bool Equivalent(IReadOnlyList<PurchaseDiscount> current, IReadOnlyList<NormalizedImport> desired)
    {
        if (current.Count != desired.Count) return false;
        var currentKeys = current.Select(x => Key(
                x.PurchaseItemId, NormalizeType(x.Type), Cap(Clean(x.Label) ?? DefaultLabel(x.Type), 250),
                x.Amount, x.Percentage, CapNullable(Clean(x.CouponCode), 120), CapNullable(Clean(x.RawText), 1000), x.Confidence))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var desiredKeys = desired.Select(x => Key(
                x.PurchaseItemId, x.Type, x.Label, x.Amount, x.Percentage, x.CouponCode, x.RawText, x.Confidence))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        return currentKeys.SequenceEqual(desiredKeys, StringComparer.Ordinal);
    }

    private static string Key(
        Guid? itemId,
        string type,
        string label,
        decimal amount,
        decimal? percentage,
        string? couponCode,
        string? rawText,
        decimal? confidence) => string.Join("\u001f",
        itemId?.ToString("N") ?? string.Empty,
        type,
        label,
        amount.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture),
        percentage?.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        couponCode ?? string.Empty,
        rawText ?? string.Empty,
        confidence?.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

    private static string NormalizeType(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "other" : value.Trim().ToLowerInvariant();

    private static string NormalizeSource(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "import" : value.Trim().ToLowerInvariant();
        return source.Length <= 32 ? source : source[..32];
    }

    private static string DefaultLabel(string? type) => NormalizeType(type) switch
    {
        "coupon" => "Coupon",
        "loyalty" => "Loyalty discount",
        "multibuy" => "Multibuy promotion",
        "bundle" => "Bundle promotion",
        "employee" => "Employee discount",
        "percentage" => "Percentage discount",
        "promotion" => "Promotion",
        _ => "Price reduction"
    };

    private static object Dto(PurchaseDiscount x) => new
    {
        x.Id, x.PurchaseId, x.PurchaseItemId, x.Type, x.Label, x.Amount, x.Percentage,
        x.CouponCode, x.RawText, x.Source, x.Confidence, x.CreatedAt, x.UpdatedAt
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];
    private static string? CapNullable(string? value, int max) => value is null ? null : Cap(value, max);

    private sealed record NormalizedImport(
        Guid? PurchaseItemId,
        string Type,
        string Label,
        decimal Amount,
        decimal? Percentage,
        string? CouponCode,
        string? RawText,
        decimal? Confidence);
}

public static class PurchaseDiscountEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseDiscountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases/{purchaseId:guid}/discounts").WithTags("Purchases");
        group.MapGet("/", async (Guid purchaseId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDiscountService service, CancellationToken ct) =>
            Map(await service.ListAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, ct)));
        group.MapPost("/", async (Guid purchaseId, Guid fullWorthSpaceId, PurchaseDiscountMutationWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDiscountService service, CancellationToken ct) =>
            Map(await service.CreateAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, request, ct), true));
        group.MapPatch("/{discountId:guid}", async (Guid purchaseId, Guid discountId, Guid fullWorthSpaceId, PurchaseDiscountMutationWrite request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDiscountService service, CancellationToken ct) =>
            Map(await service.UpdateAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, discountId, request, ct)));
        group.MapDelete("/{discountId:guid}", async (Guid purchaseId, Guid discountId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDiscountService service, CancellationToken ct) =>
        {
            var result = await service.DeleteAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, discountId, ct);
            return result switch
            {
                PurchaseMutationResult.Success => Results.NoContent(),
                PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        });
        return app;
    }

    private static IResult Map((PurchaseMutationResult Result, object? Value, string? Error) outcome, bool created = false) => outcome.Result switch
    {
        PurchaseMutationResult.Success when created => Results.Created(string.Empty, outcome.Value),
        PurchaseMutationResult.Success => Results.Ok(outcome.Value),
        PurchaseMutationResult.Invalid => Results.BadRequest(new { error = outcome.Error }),
        PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.NotFound()
    };
}
