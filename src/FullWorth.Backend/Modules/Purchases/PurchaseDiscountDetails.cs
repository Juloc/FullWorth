using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public static class PurchaseDiscountTypes
{
    public static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "price_reduction", "percentage", "coupon", "loyalty", "multibuy", "bundle",
        "employee", "promotion", "other"
    };
}

public sealed record PurchaseItemFinancialWrite(
    Guid PurchaseItemId,
    decimal? OriginalUnitPrice,
    decimal DiscountAmount,
    string? DiscountLabel,
    decimal DepositAmount);

public sealed record PurchaseDiscountWrite(
    Guid? Id,
    Guid? PurchaseItemId,
    string Type,
    string Label,
    decimal Amount,
    decimal? Percentage,
    string? CouponCode,
    string? RawText,
    string Source,
    decimal? Confidence);

public sealed record PurchaseFinancialWrite(
    decimal? SubtotalAmount,
    decimal DiscountAmount,
    decimal DepositAmount,
    decimal? TaxAmount,
    decimal RoundingAmount,
    IReadOnlyList<PurchaseItemFinancialWrite>? Items,
    IReadOnlyList<PurchaseDiscountWrite>? Discounts);

public sealed record PurchaseFinancialView(
    Guid PurchaseId,
    string Currency,
    decimal TotalAmount,
    decimal? SubtotalAmount,
    decimal DiscountAmount,
    decimal DepositAmount,
    decimal? TaxAmount,
    decimal RoundingAmount,
    decimal? CalculatedTotal,
    decimal? CalculationDifference,
    IReadOnlyList<PurchaseItemFinancialRow> Items,
    IReadOnlyList<PurchaseDiscountRow> Discounts);

public sealed class PurchaseFinancialRow
{
    public Guid PurchaseId { get; set; }
    public string Currency { get; set; } = "EUR";
    public decimal TotalAmount { get; set; }
    public decimal? SubtotalAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal RoundingAmount { get; set; }
}

public sealed class PurchaseItemFinancialRow
{
    public Guid PurchaseItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? OriginalUnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? DiscountLabel { get; set; }
    public decimal DepositAmount { get; set; }
}

public sealed class PurchaseDiscountRow
{
    public Guid Id { get; set; }
    public Guid PurchaseId { get; set; }
    public Guid? PurchaseItemId { get; set; }
    public string Type { get; set; } = "other";
    public string Label { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal? Percentage { get; set; }
    public string? CouponCode { get; set; }
    public string? RawText { get; set; }
    public string Source { get; set; } = "manual";
    public decimal? Confidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PurchaseDiscountDetailsStore(
    FullWorthDbContext db,
    PurchaseAuthorizationStore authorization)
{
    public async Task<PurchaseFinancialView?> GetAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct) == PurchaseAccessLevel.None)
            return null;

        var financial = await db.Database.SqlQuery<PurchaseFinancialRow>($"""
            SELECT "Id" AS "PurchaseId", "Currency", "TotalAmount", "SubtotalAmount",
                   COALESCE("DiscountAmount", 0) AS "DiscountAmount", COALESCE("DepositAmount", 0) AS "DepositAmount",
                   "TaxAmount", "RoundingAmount"
            FROM "Purchases"
            WHERE "Id" = {purchaseId} AND "FullWorthSpaceId" = {fullWorthSpaceId}
            """).SingleOrDefaultAsync(ct);
        if (financial is null) return null;

        var items = await db.Database.SqlQuery<PurchaseItemFinancialRow>($"""
            SELECT "Id" AS "PurchaseItemId", "Name", "OriginalUnitPrice",
                   COALESCE("DiscountAmount", 0) AS "DiscountAmount", "DiscountLabel", COALESCE("DepositAmount", 0) AS "DepositAmount"
            FROM "PurchaseItems"
            WHERE "PurchaseId" = {purchaseId}
            ORDER BY "CreatedAt", "Id"
            """).ToListAsync(ct);
        var discounts = await db.Database.SqlQuery<PurchaseDiscountRow>($"""
            SELECT "Id", "PurchaseId", "PurchaseItemId", "Type", "Label", "Amount", "Percentage", "CouponCode",
                   "RawText", "Source", "Confidence", "CreatedAt", "UpdatedAt"
            FROM "PurchaseDiscounts"
            WHERE "PurchaseId" = {purchaseId}
            ORDER BY "CreatedAt", "Id"
            """).ToListAsync(ct);

        decimal? calculated = financial.SubtotalAmount.HasValue
            ? financial.SubtotalAmount.Value - financial.DiscountAmount + financial.DepositAmount + financial.RoundingAmount
            : null;
        var difference = calculated.HasValue ? financial.TotalAmount - calculated.Value : (decimal?)null;
        return new(
            financial.PurchaseId, financial.Currency, financial.TotalAmount, financial.SubtotalAmount,
            financial.DiscountAmount, financial.DepositAmount, financial.TaxAmount, financial.RoundingAmount,
            calculated, difference, items, discounts);
    }

    public async Task<PurchaseMutationResult> SaveAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid purchaseId,
        PurchaseFinancialWrite request,
        CancellationToken ct)
    {
        var access = await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return PurchaseMutationResult.NotFound;
        if (access != PurchaseAccessLevel.Write) return PurchaseMutationResult.Forbidden;

        ValidateAmounts(request);
        var itemWrites = request.Items ?? [];
        var discountWrites = request.Discounts ?? [];
        var referencedItemIds = itemWrites.Select(x => x.PurchaseItemId)
            .Concat(discountWrites.Where(x => x.PurchaseItemId.HasValue).Select(x => x.PurchaseItemId!.Value))
            .Distinct().ToArray();
        if (referencedItemIds.Length > 0)
        {
            var valid = await db.PurchaseItems.AsNoTracking()
                .CountAsync(x => x.PurchaseId == purchaseId && referencedItemIds.Contains(x.Id), ct);
            if (valid != referencedItemIds.Length) return PurchaseMutationResult.NotFound;
        }

        var now = DateTimeOffset.UtcNow;
        // SaveAsync is also used inside the legacy item-replace compatibility transaction. Own a
        // transaction only when the caller has not already opened one; otherwise participate in the
        // ambient EF transaction so item replacement + financial metadata remain atomic.
        await using var ownedTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Purchases"
            SET "SubtotalAmount" = {request.SubtotalAmount},
                "DiscountAmount" = {request.DiscountAmount},
                "DepositAmount" = {request.DepositAmount},
                "TaxAmount" = {request.TaxAmount},
                "RoundingAmount" = {request.RoundingAmount},
                "UpdatedAt" = {now}
            WHERE "Id" = {purchaseId} AND "FullWorthSpaceId" = {fullWorthSpaceId}
            """, ct);

        foreach (var item in itemWrites)
        {
            var label = Cap(item.DiscountLabel, 250);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "PurchaseItems"
                SET "OriginalUnitPrice" = {item.OriginalUnitPrice},
                    "DiscountAmount" = {item.DiscountAmount},
                    "DiscountLabel" = {label},
                    "DepositAmount" = {item.DepositAmount},
                    "UpdatedAt" = {now}
                WHERE "Id" = {item.PurchaseItemId} AND "PurchaseId" = {purchaseId}
                """, ct);
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"PurchaseDiscounts\" WHERE \"PurchaseId\" = {purchaseId}", ct);
        foreach (var discount in discountWrites)
        {
            ValidateDiscount(discount);
            var id = discount.Id is { } candidate && candidate != Guid.Empty ? candidate : Guid.NewGuid();
            var type = discount.Type.Trim().ToLowerInvariant();
            var label = Cap(discount.Label.Trim(), 250)!;
            var coupon = Cap(discount.CouponCode?.Trim(), 120);
            var raw = Cap(discount.RawText?.Trim(), 1000);
            var source = NormalizeSource(discount.Source);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "PurchaseDiscounts"
                    ("Id", "PurchaseId", "PurchaseItemId", "Type", "Label", "Amount", "Percentage", "CouponCode",
                     "RawText", "Source", "Confidence", "CreatedAt", "UpdatedAt")
                VALUES
                    ({id}, {purchaseId}, {discount.PurchaseItemId}, {type}, {label}, {discount.Amount},
                     {discount.Percentage}, {coupon}, {raw}, {source}, {discount.Confidence}, {now}, {now})
                """, ct);
        }

        if (ownedTransaction is not null)
            await ownedTransaction.CommitAsync(ct);
        return PurchaseMutationResult.Success;
    }

    /// <summary>
    /// Stores financial metadata from a just-applied receipt extraction. Item rows are mapped in their
    /// creation order, which is the same order used by ReplaceItemsForUserAsync for that atomic review.
    /// This method never invents an original price/discount that the extractor did not provide.
    /// </summary>
    public async Task ApplyExtractionAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid purchaseId,
        PurchaseFinancialWrite request,
        IReadOnlyList<PurchaseItemFinancialWriteByIndex>? indexedItems,
        CancellationToken ct)
    {
        var itemRows = await db.Database.SqlQuery<PurchaseItemFinancialRow>($"""
            SELECT "Id" AS "PurchaseItemId", "Name", "OriginalUnitPrice",
                   COALESCE("DiscountAmount", 0) AS "DiscountAmount", "DiscountLabel", COALESCE("DepositAmount", 0) AS "DepositAmount"
            FROM "PurchaseItems"
            WHERE "PurchaseId" = {purchaseId}
            ORDER BY "CreatedAt", "Id"
            """).ToListAsync(ct);
        var mapped = new List<PurchaseItemFinancialWrite>();
        foreach (var indexed in indexedItems ?? [])
        {
            if (indexed.Index < 0 || indexed.Index >= itemRows.Count) continue;
            mapped.Add(new PurchaseItemFinancialWrite(
                itemRows[indexed.Index].PurchaseItemId,
                indexed.OriginalUnitPrice,
                indexed.DiscountAmount,
                indexed.DiscountLabel,
                indexed.DepositAmount));
        }
        await SaveAsync(userId, fullWorthSpaceId, purchaseId, request with { Items = mapped }, ct);
    }

    private static void ValidateAmounts(PurchaseFinancialWrite request)
    {
        if (request.DiscountAmount < 0 || request.DepositAmount < 0)
            throw new ArgumentException("Discount and deposit amounts must be non-negative.");
        foreach (var item in request.Items ?? [])
            if (item.DiscountAmount < 0 || item.DepositAmount < 0)
                throw new ArgumentException("Item discount and deposit amounts must be non-negative.");
        foreach (var discount in request.Discounts ?? []) ValidateDiscount(discount);
    }

    private static void ValidateDiscount(PurchaseDiscountWrite discount)
    {
        var type = discount.Type?.Trim() ?? string.Empty;
        if (!PurchaseDiscountTypes.Allowed.Contains(type)) throw new ArgumentException("Unsupported purchase discount type.");
        if (string.IsNullOrWhiteSpace(discount.Label)) throw new ArgumentException("Discount label is required.");
        if (discount.Amount < 0) throw new ArgumentException("Discount amount must be non-negative.");
        if (discount.Percentage is < 0m or > 100m) throw new ArgumentException("Discount percentage must be between 0 and 100.");
        if (discount.Confidence is < 0m or > 1m) throw new ArgumentException("Discount confidence must be between 0 and 1.");
    }

    private static string NormalizeSource(string? source)
    {
        var value = string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim().ToLowerInvariant();
        return value.Length <= 32 ? value : value[..32];
    }

    private static string? Cap(string? value, int length) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Length <= length ? value : value[..length];
}

public sealed record PurchaseItemFinancialWriteByIndex(
    int Index,
    decimal? OriginalUnitPrice,
    decimal DiscountAmount,
    string? DiscountLabel,
    decimal DepositAmount);

public static class PurchaseDiscountDetailsEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseDiscountDetailsEndpoints(this IEndpointRouteBuilder app)
    {
        Map(app.MapGroup("/api/purchases").WithTags("Purchases"));
        return app;
    }

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/financials", GetAsync);
        group.MapPut("/{id:guid}/financials", PutAsync);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseDiscountDetailsStore store,
        CancellationToken ct)
    {
        var result = await store.GetAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> PutAsync(
        Guid id,
        Guid fullWorthSpaceId,
        PurchaseFinancialWrite request,
        CurrentUserContext currentUser,
        PurchaseDiscountDetailsStore store,
        CancellationToken ct)
    {
        try
        {
            var result = await store.SaveAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, request, ct);
            return result switch
            {
                PurchaseMutationResult.Success => Results.NoContent(),
                PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                PurchaseMutationResult.NotFound => Results.NotFound(),
                _ => Results.BadRequest()
            };
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
