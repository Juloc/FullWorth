using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Purchases.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record PurchaseAutoLinkOutcome(PurchaseMutationResult Result, bool Linked);

public sealed class PurchaseCaptureService(
    FullWorthDbContext db,
    PurchaseAuthorizationStore authorization,
    PurchaseDiscountService discountService,
    ReceiptExtractionService extraction,
    IOptions<PurchaseStorageOptions> storageOptions)
{
    private readonly PurchaseStorageOptions _storage = storageOptions.Value;

    public async Task<PurchaseMutationOutcome> CaptureReceiptAsync(Guid userId, Guid fullWorthSpaceId, HttpRequest request, CancellationToken ct)
    {
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct))
            return new(PurchaseMutationResult.NotFound);
        if (!request.HasFormContentType)
            return new(PurchaseMutationResult.Invalid, Error: "multipart/form-data is required.");

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("receipt");
        if (file is null)
            return new(PurchaseMutationResult.Invalid, Error: "receipt file is required.");
        if (file.Length <= 0 || file.Length > _storage.MaxReceiptBytes)
            return new(PurchaseMutationResult.Invalid, Error: "receipt file size is invalid.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not ".jpg" and not ".jpeg" and not ".png" and not ".webp" and not ".heic" and not ".pdf")
            return new(PurchaseMutationResult.Invalid, Error: "unsupported receipt file type.");
        var header = new byte[16];
        int headerRead;
        await using (var probe = file.OpenReadStream())
            headerRead = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (!ReceiptSignature.Matches(header.AsSpan(0, headerRead), ext))
            return new(PurchaseMutationResult.Invalid, Error: "receipt file content does not match its type.");

        var merchant = form["merchant"].ToString().Trim();
        var currency = form["currency"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(currency)) currency = "EUR";
        currency = currency.ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
            return new(PurchaseMutationResult.Invalid, Error: "currency must be a three-letter code.");

        DateOnly? purchaseDate = DateOnly.TryParse(form["purchaseDate"], out var parsedDate) ? parsedDate : null;
        var total = decimal.TryParse(form["totalAmount"], System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var parsedTotal) ? parsedTotal : 0m;

        var id = Guid.NewGuid();
        var relative = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), $"{id:N}{ext}");
        var relativePortable = relative.Replace(Path.DirectorySeparatorChar, '/');
        var absolute = Path.Combine(_storage.RootPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        try
        {
            await using (var target = File.Create(absolute))
                await file.CopyToAsync(target, ct);
            var sha256 = await Sha256Async(absolute, ct);
            var duplicate = await db.PurchaseDocuments.AsNoTracking().AnyAsync(document =>
                document.Sha256 == sha256 && document.Purchase.FullWorthSpaceId == fullWorthSpaceId, ct);
            if (duplicate)
            {
                File.Delete(absolute);
                return new(PurchaseMutationResult.Invalid, Error: "This receipt file is already stored in this FullWorth Space.");
            }

            var ocr = await SafeExtractAsync(absolute, MediaType(ext), file.FileName, currency, ct);
            var merged = MergeCaptured(merchant, purchaseDate, total, currency, ocr);
            // Normalize() collapses "no deposits/subtotal found" to 0m (not null), so gate on a positive
            // amount: a zero-valued field is not OCR-populated data and must leave the purchase "captured".
            var populatedFromOcr = ocr.Merchant is not null || ocr.Total is not null || ocr.PurchaseDate is not null || ocr.Items.Count > 0 ||
                                   ocr.StructuredDiscounts is { Count: > 0 } || ocr.Subtotal > 0m || ocr.Deposits > 0m;
            var now = DateTimeOffset.UtcNow;
            var canonicalDiscounts = BuildDirectOcrDiscounts(ocr, merged.Currency);
            var recognizedDiscount = canonicalDiscounts.Sum(x => x.Amount);
            var entity = new Purchase
            {
                Id = id, FullWorthSpaceId = fullWorthSpaceId, Source = "receipt",
                Merchant = Cap(merged.Merchant, 250), MerchantRaw = string.IsNullOrWhiteSpace(merged.Merchant) ? null : Cap(merged.Merchant, 250),
                PurchaseDate = merged.Date,
                SubtotalAmount = ocr.Subtotal,
                DiscountAmount = recognizedDiscount > 0m ? recognizedDiscount : null,
                DepositAmount = ocr.Deposits,
                TaxAmount = ocr.Taxes,
                TipAmount = ocr.Tip,
                ShippingAmount = ocr.Shipping,
                FeeAmount = ocr.Fees,
                RoundingAmount = ocr.Rounding ?? 0m,
                TotalAmount = merged.Total, Currency = merged.Currency,
                Status = populatedFromOcr ? "review" : "captured", ReviewState = "needs_review",
                ReceiptImagePath = relativePortable, CreatedByUserId = userId, Visibility = "space", CreatedAt = now, UpdatedAt = now
            };

            var document = new PurchaseDocument
            {
                PurchaseId = id, DocumentType = "receipt", OriginalFileName = Cap(Path.GetFileName(file.FileName), 500),
                MediaType = MediaType(ext), StoragePath = relativePortable, Sha256 = sha256, SizeBytes = file.Length,
                Status = populatedFromOcr ? "processed" : "uploaded", CreatedAt = now, UpdatedAt = now
            };
            if (!string.Equals(ocr.Provider, "none", StringComparison.OrdinalIgnoreCase))
            {
                document.ExtractionRuns.Add(new PurchaseExtractionRun
                {
                    Provider = Cap(ocr.Provider, 64), Status = populatedFromOcr ? "completed" : "empty",
                    StartedAt = now, CompletedAt = now, NormalizedResultJson = JsonSerializer.Serialize(ocr), CreatedAt = now
                });
            }
            entity.Documents.Add(document);

            var persistedItems = new List<PurchaseItem>();
            var sort = 0;
            foreach (var li in ocr.Items)
            {
                var quantity = li.Quantity is > 0 ? li.Quantity.Value : 1m;
                var item = new PurchaseItem
                {
                    RawName = Cap(li.Name, 500), Name = Cap(li.Name, 500), Quantity = quantity,
                    QuantityUnit = PurchaseArticleCalculator.NormalizeUnit(li.QuantityUnit),
                    UnitPrice = li.UnitPrice,
                    OriginalUnitPrice = li.OriginalUnitPrice,
                    TotalPrice = PurchaseArticleCalculator.RoundMoney(li.TotalPrice ?? 0m, merged.Currency),
                    DiscountAmount = li.DiscountAmount is > 0m ? PurchaseArticleCalculator.RoundMoney(li.DiscountAmount.Value, merged.Currency) : null,
                    DiscountLabel = Clean(li.DiscountLabel),
                    DepositAmount = li.DepositAmount is > 0m ? PurchaseArticleCalculator.RoundMoney(li.DepositAmount.Value, merged.Currency) : null,
                    Currency = merged.Currency,
                    LineType = NormalizeLineType(li.LineType),
                    CategorizationSource = "none", CategoryId = null,
                    ExtractionConfidence = Math.Clamp(li.Confidence, 0m, 1m), SortOrder = sort++
                };
                item.BaseUnitPrice = PurchaseArticleCalculator.BaseUnitPrice(item.UnitPrice, item.Quantity, item.QuantityUnit, null, null, null, item.Currency);
                entity.Items.Add(item);
                persistedItems.Add(item);
            }

            var discountSource = NormalizeDiscountSource(ocr.Provider, "ocr");
            foreach (var discount in canonicalDiscounts)
            {
                PurchaseItem? linkedItem = null;
                if (discount.ItemIndex is >= 0 && discount.ItemIndex.Value < persistedItems.Count)
                    linkedItem = persistedItems[discount.ItemIndex.Value];
                entity.Discounts.Add(new PurchaseDiscount
                {
                    PurchaseItem = linkedItem,
                    Type = NormalizeDiscountType(discount.Type),
                    Label = Cap(Clean(discount.Label) ?? "OCR receipt discount", 250),
                    Amount = PurchaseArticleCalculator.RoundMoney(Math.Abs(discount.Amount), merged.Currency),
                    Percentage = discount.Percentage,
                    CouponCode = CapNullable(Clean(discount.CouponCode), 120),
                    RawText = CapNullable(Clean(discount.RawText), 1000),
                    Source = discountSource,
                    Confidence = discount.Confidence.HasValue ? Math.Clamp(discount.Confidence.Value, 0m, 1m) : null,
                    CreatedAt = now, UpdatedAt = now
                });
            }

            db.Purchases.Add(entity);
            await db.SaveChangesAsync(ct);
            var view = await authorization.GetForUserAsync(userId, fullWorthSpaceId, id, ct);
            return view is null ? new(PurchaseMutationResult.NotFound) : new(PurchaseMutationResult.Success, view);
        }
        catch
        {
            if (File.Exists(absolute)) File.Delete(absolute);
            throw;
        }
    }

    private async Task<ReceiptExtractionResult> SafeExtractAsync(string absolutePath, string contentType, string fileName, string currency, CancellationToken ct)
    {
        try
        {
            var content = await File.ReadAllBytesAsync(absolutePath, ct);
            return await extraction.ExtractAsync(new ReceiptExtractionRequest(content, contentType, fileName, currency), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return ReceiptExtractionResult.Empty("none"); }
    }

    private static List<PurchaseDiscountImport> BuildDirectOcrDiscounts(ReceiptExtractionResult ocr, string currency)
    {
        var source = NormalizeDiscountSource(ocr.Provider, "ocr");
        var result = new List<PurchaseDiscountImport>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var discount in ocr.StructuredDiscounts ?? [])
        {
            if (discount.Amount <= 0m) continue;
            var itemIndex = discount.ItemIndex is >= 0 && discount.ItemIndex.Value < ocr.Items.Count ? discount.ItemIndex : null;
            var amount = PurchaseArticleCalculator.RoundMoney(Math.Abs(discount.Amount), currency);
            var type = NormalizeDiscountType(discount.Type);
            var label = Clean(discount.Label);
            var key = $"{itemIndex}|{type}|{label}|{amount}|{discount.Percentage}|{Clean(discount.CouponCode)}|{Clean(discount.RawText)}";
            if (!keys.Add(key)) continue;
            result.Add(new PurchaseDiscountImport(
                null, type, label, amount, discount.Percentage, Clean(discount.CouponCode), Clean(discount.RawText),
                source, Math.Clamp(discount.Confidence, 0m, 1m), itemIndex));
        }

        for (var index = 0; index < ocr.Items.Count; index++)
        {
            var item = ocr.Items[index];
            var amount = Math.Max(0m, item.DiscountAmount ?? 0m);
            if (amount <= 0m || result.Any(x => x.ItemIndex == index)) continue;
            amount = PurchaseArticleCalculator.RoundMoney(amount, currency);
            result.Add(new PurchaseDiscountImport(
                null, "price_reduction", Clean(item.DiscountLabel) ?? "OCR item price reduction", amount,
                null, null, item.Name, source, item.Confidence, index));
        }

        var recognized = result.Sum(x => x.Amount);
        var aggregate = Math.Max(0m, ocr.Discounts ?? 0m);
        if (aggregate > recognized + PurchaseArticleCalculator.Tolerance(currency))
        {
            result.Add(new PurchaseDiscountImport(
                null, "other", "OCR receipt discount remainder",
                PurchaseArticleCalculator.RoundMoney(aggregate - recognized, currency),
                null, null, null, source, ocr.Confidence > 0m ? Math.Clamp(ocr.Confidence, 0m, 1m) : null));
        }
        return result;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, ct)).ToLowerInvariant();
    }

    private static string MediaType(string ext) => ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp",
        ".heic" => "image/heic", ".pdf" => "application/pdf", _ => "application/octet-stream"
    };
    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];
    private static string? CapNullable(string? value, int max) => value is null ? null : Cap(value, max);

    public static (string Merchant, DateOnly? Date, decimal Total, string Currency) MergeCaptured(
        string formMerchant, DateOnly? formDate, decimal formTotal, string formCurrency, ReceiptExtractionResult ocr) =>
        (string.IsNullOrWhiteSpace(formMerchant) ? (ocr.Merchant ?? string.Empty) : formMerchant,
         formDate ?? ocr.PurchaseDate,
         formTotal != 0m ? formTotal : (ocr.Total ?? 0m),
         formCurrency);

    public async Task<PurchaseMutationOutcome> ApplyExtractionAsync(
        Guid userId, Guid fullWorthSpaceId, Guid purchaseId, PurchaseExtractionRequest request, CancellationToken ct)
    {
        var access = await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return new(PurchaseMutationResult.NotFound);
        if (access != PurchaseAccessLevel.Write) return new(PurchaseMutationResult.Forbidden);
        if (string.IsNullOrWhiteSpace(request.Merchant)) return new(PurchaseMutationResult.Invalid, Error: "merchant is required.");
        var currency = request.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
            return new(PurchaseMutationResult.Invalid, Error: "currency must be a three-letter code.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var purchase = await WritablePurchases(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return new(PurchaseMutationResult.NotFound);

        purchase.MerchantRaw ??= purchase.Merchant;
        purchase.Merchant = request.Merchant.Trim(); purchase.PurchaseDate = request.PurchaseDate; purchase.PurchaseTime = request.PurchaseTime;
        purchase.SubtotalAmount = request.SubtotalAmount; purchase.DiscountAmount = request.DiscountAmount; purchase.DepositAmount = request.DepositAmount;
        if (request.RoundingAmount.HasValue) purchase.RoundingAmount = request.RoundingAmount.Value;
        purchase.TaxAmount = request.TaxAmount; purchase.TipAmount = request.TipAmount; purchase.ShippingAmount = request.ShippingAmount; purchase.FeeAmount = request.FeeAmount;
        purchase.TotalAmount = request.TotalAmount; purchase.Currency = currency;
        purchase.ReceiptNumber = Clean(request.ReceiptNumber); purchase.InvoiceNumber = Clean(request.InvoiceNumber); purchase.PaymentMethodText = Clean(request.PaymentMethodText);
        purchase.SourceReference = Clean(request.SourceReference); purchase.Notes = Clean(request.Notes);
        purchase.Status = "review"; purchase.ReviewState = "needs_review"; purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var normalizedItems = NormalizeExtractionItems(request.Items, request.AmountsAreCanonical, currency);
        var itemsOutcome = await authorization.ReplaceItemsForUserAsync(userId, fullWorthSpaceId, purchaseId, normalizedItems, ct);
        if (itemsOutcome.Result != PurchaseMutationResult.Success)
        {
            await transaction.RollbackAsync(ct);
            return itemsOutcome;
        }

        var discountSource = NormalizeDiscountSource(request.DiscountSource, SourceFromReference(request.SourceReference));
        var canonicalDiscounts = BuildDiscountImports(normalizedItems, request.Discounts, request.DiscountAmount, discountSource, currency);
        await discountService.ReplaceSourceDiscountsAsync(fullWorthSpaceId, purchaseId, discountSource, canonicalDiscounts, ct);

        await transaction.CommitAsync(ct);
        _ = await TryAutoLinkAsync(userId, fullWorthSpaceId, purchaseId, ct);
        var view = await authorization.GetForUserAsync(userId, fullWorthSpaceId, purchaseId, ct);
        return view is null ? new(PurchaseMutationResult.NotFound) : new(PurchaseMutationResult.Success, view);
    }

    public async Task<PurchaseMutationOutcome> ImportAmazonAsync(
        Guid userId, Guid fullWorthSpaceId, AmazonOrderImportRequest request, CancellationToken ct)
    {
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return new(PurchaseMutationResult.NotFound);
        var orderId = request.OrderId.Trim();
        if (string.IsNullOrWhiteSpace(orderId)) return new(PurchaseMutationResult.Invalid, Error: "order ID is required.");
        var currency = request.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z'))
            return new(PurchaseMutationResult.Invalid, Error: "currency must be a three-letter code.");

        var existingVisibleId = await VisiblePurchases(userId, fullWorthSpaceId)
            .Where(x => x.Source == "amazon" && x.ExternalOrderId == orderId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (existingVisibleId.HasValue && await authorization.GetAccessAsync(userId, fullWorthSpaceId, existingVisibleId.Value, ct) != PurchaseAccessLevel.Write)
            return new(PurchaseMutationResult.Forbidden);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        Purchase purchase;
        if (existingVisibleId.HasValue)
            purchase = await WritablePurchases(userId, fullWorthSpaceId).SingleOrDefaultAsync(x => x.Id == existingVisibleId.Value, ct)
                ?? throw new InvalidOperationException("Authorized Amazon purchase disappeared during update.");
        else
        {
            purchase = new Purchase
            {
                FullWorthSpaceId = fullWorthSpaceId, Source = "amazon", ExternalOrderId = orderId,
                CreatedByUserId = userId, Visibility = "space", CreatedAt = DateTimeOffset.UtcNow
            };
            db.Purchases.Add(purchase);
        }

        purchase.MerchantRaw ??= "Amazon"; purchase.Merchant = "Amazon"; purchase.PurchaseDate = request.PurchaseDate;
        purchase.SubtotalAmount = request.SubtotalAmount; purchase.DiscountAmount = request.DiscountAmount; purchase.DepositAmount = request.DepositAmount;
        purchase.TaxAmount = request.TaxAmount; purchase.ShippingAmount = request.ShippingAmount; purchase.FeeAmount = request.FeeAmount;
        purchase.RoundingAmount = request.RoundingAmount ?? purchase.RoundingAmount; purchase.TotalAmount = request.TotalAmount; purchase.Currency = currency;
        purchase.SourceReference = Clean(request.SourceReference); purchase.Status = "review"; purchase.ReviewState = "needs_review"; purchase.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var purchaseId = purchase.Id;
        db.ChangeTracker.Clear();

        var items = request.Items.Select((x, index) => new PurchaseItemWrite(
            x.CategoryId, x.Name, x.Brand, x.Sku, x.Asin, x.Quantity, x.UnitPrice, x.TotalPrice, currency, null,
            RawName: x.Name, DepositAmount: x.DepositAmount, LineType: "product", SortOrder: index,
            OriginalUnitPrice: x.OriginalUnitPrice, DiscountAmount: x.DiscountAmount, DiscountLabel: x.DiscountLabel)).ToList();
        var itemsOutcome = await authorization.ReplaceItemsForUserAsync(userId, fullWorthSpaceId, purchaseId, items, ct);
        if (itemsOutcome.Result != PurchaseMutationResult.Success)
        {
            await transaction.RollbackAsync(ct);
            return itemsOutcome;
        }

        var canonicalDiscounts = BuildDiscountImports(items, request.Discounts, request.DiscountAmount, "amazon", currency);
        await discountService.ReplaceSourceDiscountsAsync(fullWorthSpaceId, purchaseId, "amazon", canonicalDiscounts, ct);
        await transaction.CommitAsync(ct);
        _ = await TryAutoLinkAsync(userId, fullWorthSpaceId, purchaseId, ct);
        var view = await authorization.GetForUserAsync(userId, fullWorthSpaceId, purchaseId, ct);
        return view is null ? new(PurchaseMutationResult.NotFound) : new(PurchaseMutationResult.Success, view);
    }

    public async Task<PurchaseAutoLinkOutcome> TryAutoLinkAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var access = await authorization.GetAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access == PurchaseAccessLevel.None) return new(PurchaseMutationResult.NotFound, false);
        if (access != PurchaseAccessLevel.Write) return new(PurchaseMutationResult.Forbidden, false);
        var purchase = await WritablePurchases(userId, fullWorthSpaceId).AsNoTracking().SingleOrDefaultAsync(x => x.Id == purchaseId, ct);
        if (purchase is null) return new(PurchaseMutationResult.NotFound, false);
        if (purchase.TransactionId.HasValue || await db.PurchasePaymentLinks.AsNoTracking().AnyAsync(x => x.PurchaseId == purchaseId, ct) || purchase.TotalAmount == 0 || !purchase.PurchaseDate.HasValue)
            return new(PurchaseMutationResult.Success, false);

        var date = purchase.PurchaseDate.Value;
        var candidates = await OwnedTransactions(userId, fullWorthSpaceId)
            .Where(transaction => transaction.Amount < 0 && transaction.BookingDate >= date.AddDays(-4) && transaction.BookingDate <= date.AddDays(4) && transaction.Currency == purchase.Currency)
            .Select(transaction => new { transaction.Id, transaction.BookingDate, transaction.Amount, transaction.Counterparty }).ToListAsync(ct);
        var scored = candidates.Select(candidate => new { candidate.Id, Confidence = Score(purchase, candidate.Amount, candidate.BookingDate, candidate.Counterparty) })
            .OrderByDescending(candidate => candidate.Confidence).ToList();
        if (scored.Count == 0 || scored[0].Confidence < .94m) return new(PurchaseMutationResult.Success, false);
        if (scored.Count > 1 && scored[0].Confidence - scored[1].Confidence < .08m) return new(PurchaseMutationResult.Success, false);
        var result = await authorization.LinkForUserAsync(userId, fullWorthSpaceId, purchaseId, scored[0].Id, scored[0].Confidence, ct);
        return new(result, result == PurchaseMutationResult.Success);
    }

    private static List<PurchaseItemWrite> NormalizeExtractionItems(IReadOnlyList<PurchaseItemWrite> items, bool canonical, string currency)
    {
        if (canonical) return items.ToList();
        return items.Select(item =>
        {
            var type = (item.LineType ?? "product").Trim().ToLowerInvariant();
            if (type is "discount" or "coupon" or "deposit" or "pfand") return item;
            var deposit = Math.Max(0m, item.DepositAmount ?? 0m);
            if (deposit <= 0m) return item;
            return item with { TotalPrice = PurchaseArticleCalculator.RoundMoney(item.TotalPrice - deposit, currency) };
        }).ToList();
    }

    private static List<PurchaseDiscountImport> BuildDiscountImports(
        IReadOnlyList<PurchaseItemWrite> items,
        IReadOnlyList<PurchaseDiscountImport>? structured,
        decimal? aggregateDiscount,
        string source,
        string currency)
    {
        if (structured is { Count: > 0 })
            return structured.Where(x => x.Amount > 0m).Select(x => x with { Source = source }).ToList();

        var result = new List<PurchaseDiscountImport>();
        decimal recognized = 0m;
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var type = (item.LineType ?? "product").Trim().ToLowerInvariant();
            if (type is "discount" or "coupon")
            {
                var amount = Math.Max(0m, item.DiscountAmount ?? Math.Abs(item.TotalPrice));
                if (amount <= 0m) continue;
                recognized += amount;
                result.Add(new PurchaseDiscountImport(
                    PurchaseItemId: null,
                    Type: type == "coupon" ? "coupon" : "other",
                    Label: item.DiscountLabel ?? item.Name,
                    Amount: amount,
                    Percentage: null,
                    CouponCode: null,
                    RawText: item.RawName,
                    Source: source,
                    Confidence: item.ExtractionConfidence,
                    ItemIndex: null));
                continue;
            }

            var itemAmount = Math.Max(0m, item.DiscountAmount ?? 0m);
            if (itemAmount <= 0m) continue;
            recognized += itemAmount;
            result.Add(new PurchaseDiscountImport(
                PurchaseItemId: null,
                Type: "price_reduction",
                Label: item.DiscountLabel ?? "Item price reduction",
                Amount: itemAmount,
                Percentage: null,
                CouponCode: null,
                RawText: null,
                Source: source,
                Confidence: item.ExtractionConfidence,
                ItemIndex: index));
        }

        var residual = Math.Max(0m, (aggregateDiscount ?? 0m) - recognized);
        if (residual > PurchaseArticleCalculator.Tolerance(currency))
        {
            result.Add(new PurchaseDiscountImport(
                PurchaseItemId: null, Type: "other", Label: "Receipt discount",
                Amount: PurchaseArticleCalculator.RoundMoney(residual, currency), Percentage: null,
                CouponCode: null, RawText: null, Source: source, Confidence: null));
        }
        return result;
    }

    private IQueryable<Purchase> VisiblePurchases(Guid userId, Guid fullWorthSpaceId) =>
        db.Purchases.AsNoTracking().Where(purchase =>
            purchase.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
            (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.Any(link => db.Transactions.Any(transaction => transaction.Id == link.TransactionId &&
                db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId))))) &&
            (purchase.TransactionId == null || db.Transactions.Any(transaction => transaction.Id == purchase.TransactionId.Value &&
                db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId && account.Owners.Any(owner => owner.UserId == userId)))));

    private IQueryable<Purchase> WritablePurchases(Guid userId, Guid fullWorthSpaceId) =>
        db.Purchases.Where(purchase =>
            purchase.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
            (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.All(link => db.Transactions.Any(transaction => transaction.Id == link.TransactionId &&
                db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
                    account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))))) &&
            (purchase.TransactionId == null || db.Transactions.Any(transaction =>
                transaction.Id == purchase.TransactionId.Value && db.Accounts.Any(account =>
                    account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
                    account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)))));

    private IQueryable<FullWorth.Backend.Modules.Transactions.FinanceTransaction> OwnedTransactions(Guid userId, Guid fullWorthSpaceId) =>
        db.Transactions.AsNoTracking().Where(transaction => db.Accounts.Any(account =>
            account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
            account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner)));

    private static decimal Score(Purchase purchase, decimal transactionAmount, DateOnly? bookingDate, string? counterparty)
    {
        var amountDelta = Math.Abs(Math.Abs(transactionAmount) - Math.Abs(purchase.TotalAmount));
        var amountScore = Math.Max(0m, 1m - amountDelta / Math.Max(1m, Math.Abs(purchase.TotalAmount)));
        var merchantScore = !string.IsNullOrWhiteSpace(counterparty) && !string.IsNullOrWhiteSpace(purchase.Merchant) &&
                            (counterparty.Contains(purchase.Merchant, StringComparison.OrdinalIgnoreCase) || purchase.Merchant.Contains(counterparty, StringComparison.OrdinalIgnoreCase)) ? 1m : 0m;
        var dateScore = bookingDate.HasValue && purchase.PurchaseDate.HasValue
            ? Math.Max(0m, 1m - Math.Abs(bookingDate.Value.DayNumber - purchase.PurchaseDate.Value.DayNumber) / 5m) : 0m;
        return Math.Clamp(amountScore * .70m + merchantScore * .15m + dateScore * .15m, 0m, 1m);
    }

    private static string NormalizeDiscountSource(string? value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(source)) source = "extraction";
        return source.Length <= 32 ? source : source[..32];
    }

    private static string SourceFromReference(string? value)
    {
        var reference = Clean(value)?.ToLowerInvariant();
        if (reference is null) return "extraction";
        if (reference.StartsWith("codex:")) return "codex";
        if (reference.StartsWith("ocr")) return "ocr";
        if (reference.StartsWith("amazon")) return "amazon";
        return "extraction";
    }

    private static string NormalizeDiscountType(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "other" : value.Trim().ToLowerInvariant();
        return PurchaseDiscountTypes.Allowed.Contains(normalized) ? normalized : "other";
    }

    private static string NormalizeLineType(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "product" : value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "pfand" => "deposit",
            "coupon" => "discount",
            "product" or "deposit" or "discount" or "shipping" or "fee" or "tip" or "unknown" => normalized,
            _ => "product"
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
