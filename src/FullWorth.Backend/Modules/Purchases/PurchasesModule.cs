using System.Text.Json.Serialization;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Purchases;

public sealed class Purchase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    // Legacy single-payment link. Kept for backwards compatibility while PurchasePaymentLink is the
    // authoritative multi-payment model. New feature code writes the link table and mirrors this only
    // when exactly one payment is linked.
    public Guid? TransactionId { get; set; }
    public string Source { get; set; } = "receipt";
    public string Merchant { get; set; } = string.Empty;
    public Guid? MerchantId { get; set; }
    public string? MerchantRaw { get; set; }
    public string? ExternalOrderId { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public TimeOnly? PurchaseTime { get; set; }
    public string? TimeZone { get; set; }
    public decimal? SubtotalAmount { get; set; }
    /// <summary>Total recognized discount, stored as a positive saved amount.</summary>
    public decimal? DiscountAmount { get; set; }
    /// <summary>Total deposit/Pfand, stored as a positive amount added to the payment.</summary>
    public decimal? DepositAmount { get; set; }
    /// <summary>Explicit receipt/cash rounding. May be positive or negative.</summary>
    public decimal RoundingAmount { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? TipAmount { get; set; }
    public decimal? ShippingAmount { get; set; }
    public decimal? FeeAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Status { get; set; } = "review";
    public string ReviewState { get; set; } = "needs_review";
    public decimal? MatchConfidence { get; set; }
    public string? ReceiptImagePath { get; set; }
    public string? ReceiptNumber { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? PaymentMethodText { get; set; }
    public string? SourceReference { get; set; }
    public string? Notes { get; set; }
    public bool IsBookmarked { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? PaidByUserId { get; set; }
    public Guid? ForWhomUserId { get; set; }
    public string Visibility { get; set; } = "space";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<PurchaseItem> Items { get; set; } = new List<PurchaseItem>();
    public ICollection<PurchasePaymentLink> PaymentLinks { get; set; } = new List<PurchasePaymentLink>();
    public ICollection<PurchaseDocument> Documents { get; set; } = new List<PurchaseDocument>();
    public ICollection<PurchaseDiscount> Discounts { get; set; } = new List<PurchaseDiscount>();
    public ICollection<PurchaseDifferenceAcceptance> AcceptedDifferences { get; set; } = new List<PurchaseDifferenceAcceptance>();
    public ICollection<PurchaseTagLink> Tags { get; set; } = new List<PurchaseTagLink>();
}

public sealed class PurchaseItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseId { get; set; }
    // Back-reference used by EF only; ignored for JSON so serializing a Purchase with its Items (e.g. the
    // transaction detail view) does not create a Purchase→Items→Purchase cycle.
    [JsonIgnore] public Purchase Purchase { get; set; } = null!;
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid? CategoryId { get; set; }
    public string RawName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string? Asin { get; set; }
    public decimal Quantity { get; set; } = 1m;
    public string QuantityUnit { get; set; } = "piece";
    public decimal? PackageQuantity { get; set; }
    public string? PackageUnit { get; set; }
    public decimal? PackageCount { get; set; }
    /// <summary>Effective charged unit price after item-level discounts.</summary>
    public decimal? UnitPrice { get; set; }
    /// <summary>Reference/original unit price only when explicitly shown or reliably imported.</summary>
    public decimal? OriginalUnitPrice { get; set; }
    public decimal? BaseUnitPrice { get; set; }
    /// <summary>Effective charged merchandise line total after item discount; deposit is separate.</summary>
    public decimal TotalPrice { get; set; }
    /// <summary>Positive amount saved on this item.</summary>
    public decimal? DiscountAmount { get; set; }
    public string? DiscountLabel { get; set; }
    /// <summary>Positive deposit/Pfand amount associated with this item.</summary>
    public decimal? DepositAmount { get; set; }
    public decimal? TaxRate { get; set; }
    public decimal? TaxAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string LineType { get; set; } = "product";
    public string CategorizationSource { get; set; } = "none";
    public decimal? ExtractionConfidence { get; set; }
    public bool IsManuallyCorrected { get; set; }
    public bool TotalPriceOverridden { get; set; }
    public string? Notes { get; set; }
    public int SortOrder { get; set; }
    public DateOnly? ReturnDeadline { get; set; }
    public DateOnly? WarrantyEnd { get; set; }
    public string? SerialNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<PurchaseItemReturn> Returns { get; set; } = new List<PurchaseItemReturn>();
}

public sealed class PurchaseStore(FullWorthDbContext db)
{
    public Task<List<Purchase>> ListAsync(Guid? transactionId, string? source, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        ListForSpaceAsync(FullWorthSpaceDefaults.LegacyId, transactionId, source, from, to, ct);

    public async Task<List<Purchase>> ListForSpaceAsync(Guid fullWorthSpaceId, Guid? transactionId, string? source, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var q = db.Purchases.AsNoTracking().Include(x => x.Items).Where(x => x.FullWorthSpaceId == fullWorthSpaceId);
        if (transactionId.HasValue) q = q.Where(x => x.TransactionId == transactionId.Value || db.Set<PurchasePaymentLink>().Any(l => l.PurchaseId == x.Id && l.TransactionId == transactionId.Value));
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(x => x.Source == source);
        if (from.HasValue) q = q.Where(x => x.PurchaseDate >= from.Value);
        if (to.HasValue) q = q.Where(x => x.PurchaseDate <= to.Value);
        return await q.OrderByDescending(x => x.PurchaseDate).ThenByDescending(x => x.CreatedAt).ToListAsync(ct);
    }

    public Task<Purchase?> GetAsync(Guid id, CancellationToken ct) => GetForSpaceAsync(FullWorthSpaceDefaults.LegacyId, id, ct);

    public Task<Purchase?> GetForSpaceAsync(Guid fullWorthSpaceId, Guid id, CancellationToken ct) =>
        db.Purchases.AsNoTracking().Include(x => x.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Name))
            .SingleOrDefaultAsync(x => x.Id == id && x.FullWorthSpaceId == fullWorthSpaceId, ct);

    public Task<Purchase> UpsertAsync(Guid? id, PurchaseWrite request, CancellationToken ct) =>
        UpsertForSpaceAsync(FullWorthSpaceDefaults.LegacyId, id, request, ct);

    public async Task<Purchase> UpsertForSpaceAsync(Guid fullWorthSpaceId, Guid? id, PurchaseWrite request, CancellationToken ct)
    {
        await ValidateTransactionAsync(fullWorthSpaceId, request.TransactionId, ct);
        var entity = id.HasValue
            ? await db.Purchases.SingleOrDefaultAsync(x => x.Id == id.Value && x.FullWorthSpaceId == fullWorthSpaceId, ct)
            : null;
        if (id.HasValue && entity is null) throw new InvalidOperationException("Purchase not found in FullWorth Space.");
        if (entity is null)
        {
            entity = new Purchase { FullWorthSpaceId = fullWorthSpaceId };
            db.Purchases.Add(entity);
        }
        entity.TransactionId = request.TransactionId;
        entity.Source = string.IsNullOrWhiteSpace(request.Source) ? "receipt" : request.Source.Trim().ToLowerInvariant();
        entity.Merchant = request.Merchant.Trim();
        entity.MerchantId = request.MerchantId;
        entity.MerchantRaw = request.MerchantRaw?.Trim();
        entity.ExternalOrderId = request.ExternalOrderId?.Trim();
        entity.PurchaseDate = request.PurchaseDate;
        entity.PurchaseTime = request.PurchaseTime;
        entity.TimeZone = request.TimeZone?.Trim();
        entity.SubtotalAmount = request.SubtotalAmount;
        entity.DiscountAmount = request.DiscountAmount;
        entity.DepositAmount = request.DepositAmount;
        entity.RoundingAmount = request.RoundingAmount ?? entity.RoundingAmount;
        entity.TaxAmount = request.TaxAmount;
        entity.TipAmount = request.TipAmount;
        entity.ShippingAmount = request.ShippingAmount;
        entity.FeeAmount = request.FeeAmount;
        entity.TotalAmount = request.TotalAmount;
        entity.Currency = request.Currency.ToUpperInvariant();
        entity.Status = string.IsNullOrWhiteSpace(request.Status) ? "review" : request.Status.Trim().ToLowerInvariant();
        entity.ReviewState = string.IsNullOrWhiteSpace(request.ReviewState) ? entity.ReviewState : request.ReviewState.Trim().ToLowerInvariant();
        entity.ReceiptNumber = request.ReceiptNumber?.Trim();
        entity.InvoiceNumber = request.InvoiceNumber?.Trim();
        entity.PaymentMethodText = request.PaymentMethodText?.Trim();
        entity.SourceReference = request.SourceReference?.Trim();
        entity.Notes = request.Notes?.Trim();
        entity.IsBookmarked = request.IsBookmarked ?? entity.IsBookmarked;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task ReplaceItemsAsync(Guid purchaseId, IReadOnlyList<PurchaseItemWrite> items, CancellationToken ct)
    {
        var purchase = await db.Purchases.Include(x => x.Items).SingleAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == FullWorthSpaceDefaults.LegacyId, ct);
        await ReplaceItemsCoreAsync(purchase, items, ct);
    }

    public async Task ReplaceItemsForSpaceAsync(Guid fullWorthSpaceId, Guid purchaseId, IReadOnlyList<PurchaseItemWrite> items, CancellationToken ct)
    {
        var purchase = await db.Purchases.Include(x => x.Items).SingleAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        await ReplaceItemsCoreAsync(purchase, items, ct);
    }

    private async Task ReplaceItemsCoreAsync(Purchase purchase, IReadOnlyList<PurchaseItemWrite> items, CancellationToken ct)
    {
        var categoryIds = items.Where(x => x.CategoryId.HasValue).Select(x => x.CategoryId!.Value).Distinct().ToArray();
        if (categoryIds.Length > 0)
        {
            var validCount = await db.Categories.AsNoTracking().CountAsync(x => x.FullWorthSpaceId == purchase.FullWorthSpaceId && categoryIds.Contains(x.Id), ct);
            if (validCount != categoryIds.Length) throw new InvalidOperationException("Purchase item category must belong to the Purchase FullWorth Space.");
        }
        var productIds = items.Where(x => x.ProductId.HasValue).Select(x => x.ProductId!.Value).Distinct().ToArray();
        if (productIds.Length > 0)
        {
            var validCount = await db.Set<Product>().AsNoTracking().CountAsync(x => x.FullWorthSpaceId == purchase.FullWorthSpaceId && productIds.Contains(x.Id), ct);
            if (validCount != productIds.Length) throw new InvalidOperationException("Purchase item product must belong to the Purchase FullWorth Space.");
        }

        db.PurchaseItems.RemoveRange(purchase.Items);
        var sort = 0;
        foreach (var item in items)
        {
            purchase.Items.Add(ToEntity(item, purchase.Currency, sort++));
        }
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await ApplyItemRulesAsync(purchase.FullWorthSpaceId, purchase.Items, ct);
        await db.SaveChangesAsync(ct);
    }

    private static PurchaseItem ToEntity(PurchaseItemWrite item, string purchaseCurrency, int fallbackSort) => new()
    {
        ProductId = item.ProductId,
        CategoryId = item.CategoryId,
        RawName = string.IsNullOrWhiteSpace(item.RawName) ? item.Name.Trim() : item.RawName.Trim(),
        Name = item.Name.Trim(),
        Brand = item.Brand?.Trim(),
        Sku = item.Sku?.Trim(),
        Barcode = item.Barcode?.Trim(),
        Asin = item.Asin?.Trim(),
        Quantity = item.Quantity <= 0 ? 1 : item.Quantity,
        QuantityUnit = string.IsNullOrWhiteSpace(item.QuantityUnit) ? "piece" : item.QuantityUnit.Trim().ToLowerInvariant(),
        PackageQuantity = item.PackageQuantity,
        PackageUnit = item.PackageUnit?.Trim().ToLowerInvariant(),
        PackageCount = item.PackageCount,
        UnitPrice = item.UnitPrice,
        OriginalUnitPrice = item.OriginalUnitPrice,
        BaseUnitPrice = item.BaseUnitPrice,
        TotalPrice = item.TotalPrice,
        DiscountAmount = item.DiscountAmount,
        DiscountLabel = item.DiscountLabel?.Trim(),
        DepositAmount = item.DepositAmount,
        TaxRate = item.TaxRate,
        TaxAmount = item.TaxAmount,
        Currency = string.IsNullOrWhiteSpace(item.Currency) ? purchaseCurrency : item.Currency.ToUpperInvariant(),
        LineType = string.IsNullOrWhiteSpace(item.LineType) ? "product" : item.LineType.Trim().ToLowerInvariant(),
        CategorizationSource = item.CategoryId.HasValue ? "manual" : "none",
        ExtractionConfidence = item.ExtractionConfidence,
        IsManuallyCorrected = item.IsManuallyCorrected,
        TotalPriceOverridden = item.TotalPriceOverridden,
        Notes = item.Notes?.Trim(),
        SortOrder = item.SortOrder ?? fallbackSort,
        ReturnDeadline = item.ReturnDeadline,
        WarrantyEnd = item.WarrantyEnd,
        SerialNumber = item.SerialNumber?.Trim()
    };

    public Task<List<object>> MatchCandidatesAsync(Guid purchaseId, CancellationToken ct) =>
        MatchCandidatesForSpaceAsync(FullWorthSpaceDefaults.LegacyId, purchaseId, ct);

    public async Task<List<object>> MatchCandidatesForSpaceAsync(Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var purchase = await db.Purchases.AsNoTracking().SingleAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (purchase.TransactionId.HasValue || await db.Set<PurchasePaymentLink>().AnyAsync(x => x.PurchaseId == purchase.Id, ct)) return [];
        var date = purchase.PurchaseDate ?? DateOnly.FromDateTime(DateTime.Today);
        var candidates = await db.Transactions.AsNoTracking()
            .Where(x => db.Accounts.Any(account => account.Id == x.AccountId && account.FullWorthSpaceId == fullWorthSpaceId))
            .Where(x => x.Amount < 0 && x.BookingDate >= date.AddDays(-4) && x.BookingDate <= date.AddDays(4) && x.Currency == purchase.Currency)
            .Select(x => new { x.Id, x.BookingDate, x.Amount, x.Counterparty, x.Description })
            .ToListAsync(ct);
        return candidates.Select(x =>
        {
            var amountDelta = Math.Abs(Math.Abs(x.Amount) - Math.Abs(purchase.TotalAmount));
            var amountScore = purchase.TotalAmount == 0 ? 0m : Math.Max(0m, 1m - amountDelta / Math.Max(1m, Math.Abs(purchase.TotalAmount)));
            var merchantScore = (!string.IsNullOrWhiteSpace(x.Counterparty) && !string.IsNullOrWhiteSpace(purchase.Merchant) &&
                (x.Counterparty.Contains(purchase.Merchant, StringComparison.OrdinalIgnoreCase) || purchase.Merchant.Contains(x.Counterparty, StringComparison.OrdinalIgnoreCase))) ? 1m : 0m;
            var dateScore = x.BookingDate.HasValue ? Math.Max(0m, 1m - Math.Abs(x.BookingDate.Value.DayNumber - date.DayNumber) / 5m) : 0m;
            var confidence = Math.Clamp(amountScore * .65m + merchantScore * .20m + dateScore * .15m, 0m, 1m);
            return (object)new { x.Id, x.BookingDate, x.Amount, x.Counterparty, x.Description, confidence };
        }).OrderByDescending(x => (decimal)x.GetType().GetProperty("confidence")!.GetValue(x)!).Take(10).ToList();
    }

    public Task LinkAsync(Guid purchaseId, Guid transactionId, decimal? confidence, CancellationToken ct) =>
        LinkForSpaceAsync(FullWorthSpaceDefaults.LegacyId, purchaseId, transactionId, confidence, ct);

    public async Task LinkForSpaceAsync(Guid fullWorthSpaceId, Guid purchaseId, Guid transactionId, decimal? confidence, CancellationToken ct)
    {
        var purchase = await db.Purchases.SingleAsync(x => x.Id == purchaseId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        await ValidateTransactionAsync(fullWorthSpaceId, transactionId, ct);
        purchase.TransactionId = transactionId;
        purchase.MatchConfidence = confidence;
        purchase.Status = "confirmed";
        purchase.ReviewState = "confirmed";
        purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task ApplyItemRulesAsync(Guid fullWorthSpaceId, IEnumerable<PurchaseItem> items, CancellationToken ct)
    {
        var rules = await db.CategorizationRules.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.IsEnabled && x.Target == "item")
            .OrderBy(x => x.Priority).ThenBy(x => x.Id).ToListAsync(ct);
        foreach (var item in items)
        {
            if (item.CategorizationSource == "manual") continue;
            foreach (var rule in rules)
            {
                var text = rule.MatchField switch
                {
                    "brand" => item.Brand,
                    "sku" => item.Sku,
                    "asin" => item.Asin,
                    "barcode" => item.Barcode,
                    _ => item.Name
                };
                if (!Matches(text, rule.Pattern, rule.MatchMode)) continue;
                var abs = Math.Abs(item.TotalPrice);
                if (rule.MinAmount.HasValue && abs < rule.MinAmount.Value) continue;
                if (rule.MaxAmount.HasValue && abs > rule.MaxAmount.Value) continue;
                item.CategoryId = rule.CategoryId;
                item.CategorizationSource = "rule";
                if (rule.StopProcessing) break;
            }
        }
    }

    private async Task ValidateTransactionAsync(Guid fullWorthSpaceId, Guid? transactionId, CancellationToken ct)
    {
        if (!transactionId.HasValue) return;
        var valid = await db.Transactions.AsNoTracking().AnyAsync(x =>
            x.Id == transactionId.Value &&
            db.Accounts.Any(account => account.Id == x.AccountId && account.FullWorthSpaceId == fullWorthSpaceId), ct);
        if (!valid) throw new InvalidOperationException("Purchase transaction must belong to the same FullWorth Space.");
    }

    private static bool Matches(string? text, string pattern, string mode)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return mode switch
        {
            "equals" => string.Equals(text.Trim(), pattern.Trim(), StringComparison.OrdinalIgnoreCase),
            "starts_with" => text.Trim().StartsWith(pattern.Trim(), StringComparison.OrdinalIgnoreCase),
            "ends_with" => text.Trim().EndsWith(pattern.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => text.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase)
        };
    }
}

public sealed record PurchaseWrite(
    Guid? TransactionId,
    string Source,
    string Merchant,
    string? ExternalOrderId,
    DateOnly? PurchaseDate,
    decimal TotalAmount,
    string Currency,
    string Status,
    string? SourceReference,
    string? Notes,
    Guid? MerchantId = null,
    string? MerchantRaw = null,
    TimeOnly? PurchaseTime = null,
    string? TimeZone = null,
    decimal? SubtotalAmount = null,
    decimal? DiscountAmount = null,
    decimal? DepositAmount = null,
    decimal? TaxAmount = null,
    decimal? TipAmount = null,
    decimal? ShippingAmount = null,
    decimal? FeeAmount = null,
    string? ReviewState = null,
    string? ReceiptNumber = null,
    string? InvoiceNumber = null,
    string? PaymentMethodText = null,
    bool? IsBookmarked = null,
    decimal? RoundingAmount = null);

public sealed record PurchaseItemWrite(
    Guid? CategoryId,
    string Name,
    string? Brand,
    string? Sku,
    string? Asin,
    decimal Quantity,
    decimal? UnitPrice,
    decimal TotalPrice,
    string Currency,
    string? Notes,
    Guid? ProductId = null,
    string? RawName = null,
    string? Barcode = null,
    string? QuantityUnit = null,
    decimal? PackageQuantity = null,
    string? PackageUnit = null,
    decimal? PackageCount = null,
    decimal? BaseUnitPrice = null,
    decimal? DiscountAmount = null,
    decimal? DepositAmount = null,
    decimal? TaxRate = null,
    decimal? TaxAmount = null,
    string? LineType = null,
    decimal? ExtractionConfidence = null,
    bool IsManuallyCorrected = false,
    bool TotalPriceOverridden = false,
    int? SortOrder = null,
    DateOnly? ReturnDeadline = null,
    DateOnly? WarrantyEnd = null,
    string? SerialNumber = null,
    decimal? OriginalUnitPrice = null,
    string? DiscountLabel = null);

public sealed record LinkPurchaseRequest(Guid TransactionId, decimal? Confidence);

public static class PurchaseEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases").WithTags("Purchases");
        group.MapGet("/", async (Guid? transactionId, string? source, DateOnly? from, DateOnly? to, PurchaseStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(transactionId, source, from, to, ct)));
        group.MapGet("/{id:guid}", async (Guid id, PurchaseStore store, CancellationToken ct) => { var x = await store.GetAsync(id, ct); return x is null ? Results.NotFound() : Results.Ok(x); });
        group.MapPost("/", async (PurchaseWrite request, PurchaseStore store, CancellationToken ct) => Results.Ok(await store.UpsertAsync(null, request, ct)));
        group.MapPut("/{id:guid}", async (Guid id, PurchaseWrite request, PurchaseStore store, CancellationToken ct) => Results.Ok(await store.UpsertAsync(id, request, ct)));
        group.MapPut("/{id:guid}/items", async (Guid id, List<PurchaseItemWrite> items, PurchaseStore store, CancellationToken ct) => { await store.ReplaceItemsAsync(id, items, ct); return Results.NoContent(); });
        group.MapGet("/{id:guid}/match-candidates", async (Guid id, PurchaseStore store, CancellationToken ct) => Results.Ok(await store.MatchCandidatesAsync(id, ct)));
        group.MapPost("/{id:guid}/link", async (Guid id, LinkPurchaseRequest request, PurchaseStore store, CancellationToken ct) => { await store.LinkAsync(id, request.TransactionId, request.Confidence, ct); return Results.NoContent(); });
        return app;
    }
}