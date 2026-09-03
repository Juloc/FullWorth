using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Purchases.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases;

public sealed record ApplyExtractionRunRequest(
    bool ApplyMerchant = true,
    bool ApplyDate = true,
    bool ApplyTotal = true,
    bool ApplyCurrency = false,
    bool ReplaceItems = false);

public sealed record PurchaseDocumentFile(string AbsolutePath, string MediaType, string FileName);
public sealed record PurchaseDocumentMutation(PurchaseMutationResult Result, object? Value = null, string? Error = null, bool Duplicate = false);

public sealed class PurchaseDocumentService(
    FullWorthDbContext db,
    ReceiptExtractionService extraction,
    PurchaseDiscountService discountService,
    IOptions<PurchaseStorageOptions> storageOptions)
{
    private readonly PurchaseStorageOptions storage = storageOptions.Value;

    public async Task<object?> ListAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (!await VisiblePurchases(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct)) return null;
        return await db.Set<PurchaseDocument>().AsNoTracking().Where(x => x.PurchaseId == purchaseId)
            .OrderBy(x => x.CreatedAt)
            .Select(x => new { x.Id, x.DocumentType, x.OriginalFileName, x.MediaType, x.Sha256, x.PageCount, x.SizeBytes, x.Status, x.CreatedAt, x.UpdatedAt, extractionCount = x.ExtractionRuns.Count() })
            .ToListAsync(ct);
    }

    public async Task<PurchaseDocumentMutation> UploadAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, HttpRequest request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return new(access);
        if (!request.HasFormContentType) return new(PurchaseMutationResult.Invalid, Error: "multipart/form-data is required.");
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("document") ?? form.Files.GetFile("receipt");
        if (file is null) return new(PurchaseMutationResult.Invalid, Error: "document file is required.");
        if (file.Length <= 0 || file.Length > storage.MaxReceiptBytes) return new(PurchaseMutationResult.Invalid, Error: "document file size is invalid.");
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not ".jpg" and not ".jpeg" and not ".png" and not ".webp" and not ".heic" and not ".pdf")
            return new(PurchaseMutationResult.Invalid, Error: "unsupported document file type.");
        var header = new byte[16];
        int read;
        await using (var stream = file.OpenReadStream()) read = await stream.ReadAtLeastAsync(header, header.Length, false, ct);
        if (!ReceiptSignature.Matches(header.AsSpan(0, read), ext)) return new(PurchaseMutationResult.Invalid, Error: "document file content does not match its type.");

        byte[] bytes;
        await using (var source = file.OpenReadStream())
        await using (var memory = new MemoryStream())
        {
            await source.CopyToAsync(memory, ct);
            bytes = memory.ToArray();
        }
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var force = string.Equals(form["force"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        if (!force)
        {
            var duplicate = await db.Set<PurchaseDocument>().AsNoTracking()
                .Where(x => x.Sha256 == sha && x.Purchase.FullWorthSpaceId == fullWorthSpaceId)
                .Where(x => (x.Purchase.Visibility != "private" || x.Purchase.CreatedByUserId == userId) &&
                    (!x.Purchase.PaymentLinks.Any() || x.Purchase.PaymentLinks.Any(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(account => account.Id == tx.AccountId && account.Owners.Any(owner => owner.UserId == userId))))) &&
                    (x.Purchase.TransactionId == null || db.Transactions.Any(tx => tx.Id == x.Purchase.TransactionId && db.Accounts.Any(account => account.Id == tx.AccountId && account.Owners.Any(owner => owner.UserId == userId)))))
                .Select(x => new { x.Id, x.PurchaseId, x.OriginalFileName, x.CreatedAt, x.Purchase.Merchant, x.Purchase.PurchaseDate, x.Purchase.TotalAmount, x.Purchase.Currency })
                .FirstOrDefaultAsync(ct);
            if (duplicate is not null) return new(PurchaseMutationResult.Invalid, duplicate, "Duplicate document detected.", true);
        }

        var documentType = NormalizeDocumentType(form["documentType"].ToString());
        var id = Guid.NewGuid();
        var relative = Path.Combine(DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), $"{id:N}{ext}").Replace(Path.DirectorySeparatorChar, '/');
        var absolute = SafeAbsolute(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        try
        {
            await File.WriteAllBytesAsync(absolute, bytes, ct);
            var now = DateTimeOffset.UtcNow;
            var document = new PurchaseDocument
            {
                Id = id, PurchaseId = purchaseId, DocumentType = documentType,
                OriginalFileName = SafeFileName(file.FileName), MediaType = ContentType(ext), StoragePath = relative,
                Sha256 = sha, SizeBytes = file.Length, Status = "uploaded", CreatedAt = now, UpdatedAt = now
            };
            db.Add(document);
            await db.SaveChangesAsync(ct);
            return new(PurchaseMutationResult.Success, new { document.Id, document.DocumentType, document.OriginalFileName, document.MediaType, document.SizeBytes, document.Status, document.CreatedAt });
        }
        catch
        {
            if (File.Exists(absolute)) File.Delete(absolute);
            throw;
        }
    }

    public async Task<PurchaseDocumentFile?> GetContentAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid documentId, CancellationToken ct)
    {
        var doc = await VisiblePurchases(userId, fullWorthSpaceId).Where(p => p.Id == purchaseId)
            .SelectMany(p => p.Documents.Where(d => d.Id == documentId))
            .Select(d => new { d.StoragePath, d.MediaType, d.OriginalFileName }).SingleOrDefaultAsync(ct);
        if (doc is null) return null;
        var absolute = SafeAbsolute(doc.StoragePath);
        if (!File.Exists(absolute)) return null;
        return new(absolute, SafeMediaType(doc.MediaType), SafeFileName(doc.OriginalFileName));
    }

    public async Task<PurchaseMutationResult> DeleteAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid documentId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return access;
        var doc = await db.Set<PurchaseDocument>().SingleOrDefaultAsync(x => x.Id == documentId && x.PurchaseId == purchaseId, ct);
        if (doc is null) return PurchaseMutationResult.NotFound;
        var absolute = SafeAbsolute(doc.StoragePath);
        try { if (File.Exists(absolute)) File.Delete(absolute); }
        catch { return PurchaseMutationResult.Invalid; }
        db.Remove(doc);
        await db.SaveChangesAsync(ct);
        return PurchaseMutationResult.Success;
    }

    public async Task<PurchaseDocumentMutation> ExtractAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid documentId, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return new(access);
        var doc = await db.Set<PurchaseDocument>().Include(x => x.Purchase).SingleOrDefaultAsync(x => x.Id == documentId && x.PurchaseId == purchaseId && x.Purchase.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (doc is null) return new(PurchaseMutationResult.NotFound);
        var absolute = SafeAbsolute(doc.StoragePath);
        if (!File.Exists(absolute)) return new(PurchaseMutationResult.Invalid, Error: "Stored document is missing.");

        var run = new PurchaseExtractionRun { PurchaseDocumentId = doc.Id, Provider = extraction.ActiveProvider, Status = "processing", StartedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow };
        db.Add(run);
        doc.Status = "processing";
        doc.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        try
        {
            var result = await ExtractDocumentAsync(doc, absolute, ct);
            run.Status = "completed";
            run.Provider = result.Provider;
            run.NormalizedResultJson = JsonSerializer.Serialize(result);
            run.CompletedAt = DateTimeOffset.UtcNow;
            doc.Status = "review";
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            if (doc.Purchase.ReviewState == "confirmed")
            {
                doc.Purchase.Status = "review";
                doc.Purchase.ReviewState = "needs_review";
                doc.Purchase.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            return new(PurchaseMutationResult.Success, new { run.Id, run.Provider, run.Status, run.StartedAt, run.CompletedAt, result });
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            run.Status = "failed";
            run.ErrorCode = "extraction_failed";
            run.ErrorMessageSafe = "Document extraction failed.";
            run.CompletedAt = DateTimeOffset.UtcNow;
            doc.Status = "failed";
            doc.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return new(PurchaseMutationResult.Invalid, new { run.Id, run.Status }, "Document extraction failed.");
        }
    }

    public async Task<object?> ExtractionsAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid documentId, CancellationToken ct)
    {
        if (!await VisiblePurchases(userId, fullWorthSpaceId).AnyAsync(p => p.Id == purchaseId && p.Documents.Any(d => d.Id == documentId), ct)) return null;
        return await db.Set<PurchaseExtractionRun>().AsNoTracking().Where(x => x.PurchaseDocumentId == documentId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.Provider, x.ProviderVersion, x.Status, x.StartedAt, x.CompletedAt, x.ErrorCode, x.ErrorMessageSafe, x.CreatedAt })
            .ToListAsync(ct);
    }

    public async Task<PurchaseDocumentMutation> ApplyRunAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, Guid runId, ApplyExtractionRunRequest request, CancellationToken ct)
    {
        var access = await WriteAccessAsync(userId, fullWorthSpaceId, purchaseId, ct);
        if (access != PurchaseMutationResult.Success) return new(access);
        var run = await db.Set<PurchaseExtractionRun>().Include(x => x.PurchaseDocument).ThenInclude(x => x.Purchase)
            .SingleOrDefaultAsync(x => x.Id == runId && x.PurchaseDocument.PurchaseId == purchaseId && x.PurchaseDocument.Purchase.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (run is null) return new(PurchaseMutationResult.NotFound);
        if (run.Status != "completed" || string.IsNullOrWhiteSpace(run.NormalizedResultJson)) return new(PurchaseMutationResult.Invalid, Error: "Extraction run is not completed.");
        var extracted = JsonSerializer.Deserialize<ReceiptExtractionResult>(run.NormalizedResultJson);
        if (extracted is null) return new(PurchaseMutationResult.Invalid, Error: "Extraction result is invalid.");
        extracted = ReceiptExtractionService.Normalize(extracted);
        var purchase = run.PurchaseDocument.Purchase;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (request.ApplyMerchant && !string.IsNullOrWhiteSpace(extracted.Merchant)) { purchase.MerchantRaw = extracted.Merchant; purchase.Merchant = extracted.Merchant.Trim(); }
        if (request.ApplyDate && extracted.PurchaseDate.HasValue) purchase.PurchaseDate = extracted.PurchaseDate;
        if (request.ApplyCurrency && !string.IsNullOrWhiteSpace(extracted.Currency)) purchase.Currency = extracted.Currency!;
        if (request.ApplyTotal)
        {
            if (extracted.Total.HasValue) purchase.TotalAmount = extracted.Total.Value;
            purchase.SubtotalAmount = extracted.Subtotal;
            purchase.DepositAmount = extracted.Deposits;
            purchase.TaxAmount = extracted.Taxes;
            purchase.RoundingAmount = extracted.Rounding ?? 0m;
            purchase.TipAmount = extracted.Tip;
            purchase.ShippingAmount = extracted.Shipping;
            purchase.FeeAmount = extracted.Fees;
        }
        purchase.Status = "review";
        purchase.ReviewState = "needs_review";
        purchase.UpdatedAt = DateTimeOffset.UtcNow;

        if (request.ReplaceItems)
        {
            var generatedAllocationIds = await db.Set<PurchaseAllocationLink>()
                .Where(x => x.PurchaseId == purchaseId)
                .Select(x => x.TransactionAllocationId)
                .ToListAsync(ct);
            if (generatedAllocationIds.Count > 0)
            {
                var generated = await db.TransactionAllocations.Where(x => generatedAllocationIds.Contains(x.Id)).ToListAsync(ct);
                if (generated.Count > 0) db.TransactionAllocations.RemoveRange(generated);
            }

            var existing = await db.PurchaseItems.Where(x => x.PurchaseId == purchaseId).ToListAsync(ct);
            var ids = existing.Select(x => x.Id).ToArray();
            var allocations = await db.TransactionAllocations.Where(x => x.PurchaseItemId.HasValue && ids.Contains(x.PurchaseItemId.Value)).ToListAsync(ct);
            foreach (var allocation in allocations) allocation.PurchaseItemId = null;
            db.PurchaseItems.RemoveRange(existing);
            var sort = 0;
            foreach (var item in extracted.Items)
            {
                var quantity = item.Quantity is > 0m ? item.Quantity.Value : 1m;
                var quantityUnit = PurchaseArticleCalculator.NormalizeUnit(item.QuantityUnit);
                var currency = purchase.Currency;
                db.PurchaseItems.Add(new PurchaseItem
                {
                    PurchaseId = purchaseId,
                    RawName = item.Name,
                    Name = item.Name,
                    Quantity = quantity,
                    QuantityUnit = quantityUnit,
                    UnitPrice = item.UnitPrice,
                    OriginalUnitPrice = item.OriginalUnitPrice,
                    TotalPrice = PurchaseArticleCalculator.RoundMoney(item.TotalPrice ?? 0m, currency),
                    BaseUnitPrice = PurchaseArticleCalculator.BaseUnitPrice(item.UnitPrice, quantity, quantityUnit, null, null, null, currency),
                    DiscountAmount = item.DiscountAmount,
                    DiscountLabel = item.DiscountLabel,
                    DepositAmount = item.DepositAmount,
                    Currency = currency,
                    LineType = NormalizeItemLineType(item.LineType),
                    CategorizationSource = "none",
                    ExtractionConfidence = item.Confidence,
                    SortOrder = sort++,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }

        var accepted = await db.Set<PurchaseDifferenceAcceptance>().Where(x => x.PurchaseId == purchaseId).ToListAsync(ct);
        if (accepted.Count > 0) db.RemoveRange(accepted);
        run.PurchaseDocument.Status = "confirmed";
        run.PurchaseDocument.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (request.ApplyTotal || request.ReplaceItems)
        {
            var source = DocumentDiscountSource(run.PurchaseDocumentId);
            var imports = BuildDiscountImports(extracted, request.ReplaceItems, source, purchase.Currency);
            await discountService.ReplaceSourceDiscountsAsync(fullWorthSpaceId, purchaseId, source, imports, ct);
        }

        await transaction.CommitAsync(ct);
        return new(PurchaseMutationResult.Success, new
        {
            purchase.Id,
            purchase.Status,
            purchase.ReviewState,
            itemCount = await db.PurchaseItems.CountAsync(x => x.PurchaseId == purchaseId, ct),
            discountCount = await db.Set<PurchaseDiscount>().CountAsync(x => x.PurchaseId == purchaseId, ct)
        });
    }

    private async Task<ReceiptExtractionResult> ExtractDocumentAsync(PurchaseDocument doc, string absolute, CancellationToken ct)
    {
        if (!string.Equals(doc.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await File.ReadAllBytesAsync(absolute, ct);
            return await extraction.ExtractAsync(new ReceiptExtractionRequest(bytes, doc.MediaType, doc.OriginalFileName, doc.Purchase.Currency), ct);
        }

        var pageCount = await ReceiptPdfRasterizer.GetPageCountAsync(absolute, storage.MaxReceiptSources, ct);
        doc.PageCount = pageCount;
        var pages = new List<ReceiptExtractionResult>(pageCount);
        for (var page = 1; page <= pageCount; page++)
        {
            var image = await ReceiptPdfRasterizer.RenderPageAsync(absolute, page, storage.MaxReceiptSources, ct);
            pages.Add(await extraction.ExtractAsync(new ReceiptExtractionRequest(
                image,
                "image/png",
                $"{Path.GetFileNameWithoutExtension(doc.OriginalFileName)}-page-{page}.png",
                doc.Purchase.Currency), ct));
        }
        return CombinePages(pages, doc.Purchase.Currency);
    }

    private static ReceiptExtractionResult CombinePages(IReadOnlyList<ReceiptExtractionResult> pages, string fallbackCurrency)
    {
        if (pages.Count == 0) return ReceiptExtractionResult.Empty("none");
        var items = new List<ReceiptLineItem>();
        var discounts = new List<ReceiptDiscount>();
        var offset = 0;
        foreach (var page in pages)
        {
            var normalized = ReceiptExtractionService.Normalize(page);
            foreach (var discount in normalized.StructuredDiscounts ?? [])
                discounts.Add(discount.ItemIndex.HasValue ? discount with { ItemIndex = discount.ItemIndex.Value + offset } : discount);
            items.AddRange(normalized.Items);
            offset += normalized.Items.Count;
        }

        var ordered = pages.Select(ReceiptExtractionService.Normalize).ToList();
        var provider = string.Join("+", ordered.Select(x => x.Provider).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(provider)) provider = "none";
        var structuredTotal = discounts.Sum(x => x.Amount);
        var declaredDiscount = ordered.Select(x => x.Discounts).LastOrDefault(x => x.HasValue);
        return ReceiptExtractionService.Normalize(new ReceiptExtractionResult(
            Provider: provider.Length <= 64 ? provider : provider[..64],
            Merchant: ordered.Select(x => x.Merchant).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            PurchaseDate: ordered.Select(x => x.PurchaseDate).FirstOrDefault(x => x.HasValue),
            Currency: ordered.Select(x => x.Currency).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? fallbackCurrency,
            Total: ordered.Select(x => x.Total).LastOrDefault(x => x.HasValue),
            Discounts: declaredDiscount ?? (structuredTotal > 0m ? structuredTotal : null),
            Deposits: ordered.Select(x => x.Deposits).LastOrDefault(x => x.HasValue),
            Taxes: ordered.Select(x => x.Taxes).LastOrDefault(x => x.HasValue),
            Items: items,
            Confidence: ordered.Count == 0 ? 0m : ordered.Average(x => x.Confidence),
            Subtotal: ordered.Select(x => x.Subtotal).LastOrDefault(x => x.HasValue),
            Rounding: ordered.Select(x => x.Rounding).LastOrDefault(x => x.HasValue),
            Tip: ordered.Select(x => x.Tip).LastOrDefault(x => x.HasValue),
            Shipping: ordered.Select(x => x.Shipping).LastOrDefault(x => x.HasValue),
            Fees: ordered.Select(x => x.Fees).LastOrDefault(x => x.HasValue),
            StructuredDiscounts: discounts));
    }

    private static List<PurchaseDiscountImport> BuildDiscountImports(ReceiptExtractionResult extracted, bool preserveItemIndexes, string source, string currency)
    {
        var result = new List<PurchaseDiscountImport>();
        foreach (var discount in extracted.StructuredDiscounts ?? [])
        {
            if (discount.Amount <= 0m) continue;
            result.Add(new PurchaseDiscountImport(
                PurchaseItemId: null,
                Type: PurchaseDiscountTypes.Allowed.Contains(discount.Type) ? discount.Type.ToLowerInvariant() : "other",
                Label: discount.Label,
                Amount: discount.Amount,
                Percentage: discount.Percentage,
                CouponCode: discount.CouponCode,
                RawText: discount.RawText,
                Source: source,
                Confidence: discount.Confidence,
                ItemIndex: preserveItemIndexes ? discount.ItemIndex : null));
        }

        if (preserveItemIndexes)
        {
            for (var index = 0; index < extracted.Items.Count; index++)
            {
                var item = extracted.Items[index];
                var amount = Math.Max(0m, item.DiscountAmount ?? 0m);
                if (amount <= 0m || result.Any(x => x.ItemIndex == index && Math.Abs(x.Amount - amount) <= PurchaseArticleCalculator.Tolerance(currency))) continue;
                result.Add(new PurchaseDiscountImport(
                    PurchaseItemId: null,
                    Type: "price_reduction",
                    Label: item.DiscountLabel ?? "OCR item price reduction",
                    Amount: amount,
                    Percentage: null,
                    CouponCode: null,
                    RawText: item.Name,
                    Source: source,
                    Confidence: item.Confidence,
                    ItemIndex: index));
            }
        }

        var recognized = result.Sum(x => x.Amount);
        var aggregate = Math.Max(0m, extracted.Discounts ?? 0m);
        var residual = PurchaseArticleCalculator.RoundMoney(aggregate - recognized, currency);
        if (residual > PurchaseArticleCalculator.Tolerance(currency))
            result.Add(new PurchaseDiscountImport(null, "other", "OCR receipt discount remainder", residual, null, null, null, source, null));
        return result;
    }

    private IQueryable<Purchase> VisiblePurchases(Guid userId, Guid fullWorthSpaceId) => db.Purchases.Where(purchase =>
        purchase.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
        (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
        (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.Any(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId))))) &&
        (purchase.TransactionId == null || db.Transactions.Any(tx => tx.Id == purchase.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId)))));

    private IQueryable<Purchase> WritablePurchases(Guid userId, Guid fullWorthSpaceId) => db.Purchases.Where(purchase =>
        purchase.FullWorthSpaceId == fullWorthSpaceId && db.FullWorthSpaceMembers.Any(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId) &&
        (purchase.Visibility != "private" || purchase.CreatedByUserId == userId) &&
        (!purchase.PaymentLinks.Any() || purchase.PaymentLinks.All(link => db.Transactions.Any(tx => tx.Id == link.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId && o.OwnershipType == AccountOwnershipTypes.Owner))))) &&
        (purchase.TransactionId == null || db.Transactions.Any(tx => tx.Id == purchase.TransactionId && db.Accounts.Any(a => a.Id == tx.AccountId && a.Owners.Any(o => o.UserId == userId && o.OwnershipType == AccountOwnershipTypes.Owner)))));

    private async Task<PurchaseMutationResult> WriteAccessAsync(Guid userId, Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        if (await WritablePurchases(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct)) return PurchaseMutationResult.Success;
        if (await VisiblePurchases(userId, fullWorthSpaceId).AnyAsync(x => x.Id == purchaseId, ct)) return PurchaseMutationResult.Forbidden;
        return PurchaseMutationResult.NotFound;
    }

    private string SafeAbsolute(string relative)
    {
        var root = Path.GetFullPath(storage.RootPath);
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Invalid document storage path.");
        return candidate;
    }

    private static string NormalizeDocumentType(string? value) => value?.Trim().ToLowerInvariant() switch
    { "invoice" => "invoice", "warranty" => "warranty", "credit_note" => "credit_note", "other" => "other", _ => "receipt" };
    private static string NormalizeItemLineType(string? value) => value?.Trim().ToLowerInvariant() switch
    { "unknown" => "unknown", _ => "product" };
    private static string DocumentDiscountSource(Guid documentId) => $"dococr-{documentId:N}"[..19];
    private static string SafeFileName(string? value)
    {
        var name = Path.GetFileName(value ?? "document");
        if (name.Length > 500) name = name[..500];
        return string.IsNullOrWhiteSpace(name) ? "document" : name;
    }
    private static string ContentType(string ext) => ext switch
    { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".heic" => "image/heic", ".pdf" => "application/pdf", _ => "application/octet-stream" };
    private static string SafeMediaType(string? value) => value switch
    { "image/jpeg" => "image/jpeg", "image/png" => "image/png", "image/webp" => "image/webp", "image/heic" => "image/heic", "application/pdf" => "application/pdf", _ => "application/octet-stream" };
}

public static class PurchaseDocumentEndpoints
{
    public static IEndpointRouteBuilder MapPurchaseDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases/{purchaseId:guid}/documents").WithTags("Purchases");
        group.MapGet("/", async (Guid purchaseId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) =>
        { var value = await service.ListAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        group.MapPost("/", async (Guid purchaseId, Guid fullWorthSpaceId, HttpRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) => Map(await service.UploadAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, request, ct), true));
        group.MapGet("/{documentId:guid}/content", async (Guid purchaseId, Guid documentId, Guid fullWorthSpaceId, HttpContext http, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) =>
        {
            var file = await service.GetContentAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, documentId, ct);
            if (file is null) return Results.NotFound();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var downloadName = file.MediaType == "application/pdf" ? file.FileName : null;
            return Results.File(file.AbsolutePath, file.MediaType, fileDownloadName: downloadName, enableRangeProcessing: true);
        });
        group.MapDelete("/{documentId:guid}", async (Guid purchaseId, Guid documentId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) => Mutation(await service.DeleteAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, documentId, ct)));
        group.MapPost("/{documentId:guid}/extract", async (Guid purchaseId, Guid documentId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) => Map(await service.ExtractAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, documentId, ct)));
        group.MapGet("/{documentId:guid}/extractions", async (Guid purchaseId, Guid documentId, Guid fullWorthSpaceId, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) =>
        { var value = await service.ExtractionsAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, documentId, ct); return value is null ? Results.NotFound() : Results.Ok(value); });
        app.MapPost("/api/purchases/{purchaseId:guid}/apply-extraction/{runId:guid}", async (Guid purchaseId, Guid runId, Guid fullWorthSpaceId, ApplyExtractionRunRequest request, FullWorth.Backend.Security.CurrentUserContext user, PurchaseDocumentService service, CancellationToken ct) => Map(await service.ApplyRunAsync(user.RequireUserId(), fullWorthSpaceId, purchaseId, runId, request, ct)));
        return app;
    }

    private static IResult Mutation(PurchaseMutationResult result) => result switch
    { PurchaseMutationResult.Success => Results.NoContent(), PurchaseMutationResult.Invalid => Results.BadRequest(), PurchaseMutationResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden), _ => Results.NotFound() };
    private static IResult Map(PurchaseDocumentMutation outcome, bool created = false) => outcome switch
    {
        { Duplicate: true } => Results.Conflict(new { error = outcome.Error, duplicate = outcome.Value }),
        { Result: PurchaseMutationResult.Success } when created => Results.Created(string.Empty, outcome.Value),
        { Result: PurchaseMutationResult.Success } => Results.Ok(outcome.Value),
        { Result: PurchaseMutationResult.Invalid } => Results.BadRequest(new { error = outcome.Error, detail = outcome.Value }),
        { Result: PurchaseMutationResult.Forbidden } => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.NotFound()
    };
}