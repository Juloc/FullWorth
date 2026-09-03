using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.Purchases.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Processes one durable logical receipt. Ordered images/PDF pages are never treated as separate
/// purchases. GPT receives the full set in one call; local OCR runs per logical source and is merged
/// with the same conservative overlap policy before the existing atomic extraction apply step.
/// </summary>
public sealed class ReceiptScanQueueProcessor(
    FullWorthDbContext db,
    ReceiptScanJobStore jobs,
    CodexReceiptBridgeClient codex,
    ReceiptExtractionService extraction,
    PurchaseCaptureService capture,
    IOptions<PurchaseStorageOptions> storageOptions,
    ILogger<ReceiptScanQueueProcessor> logger)
{
    private readonly PurchaseStorageOptions storage = storageOptions.Value;

    public async Task ProcessAsync(ReceiptScanJobRow job, CancellationToken ct)
    {
        var documentIds = new HashSet<Guid>();
        try
        {
            var purchase = await db.Purchases.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == job.PurchaseId && x.FullWorthSpaceId == job.FullWorthSpaceId, ct)
                ?? throw new InvalidOperationException("Queued purchase no longer exists.");

            var sources = await EnsureSourcesAndDocumentsAsync(purchase, job, ct);
            if (sources.Count == 0) throw new InvalidOperationException("Queued receipt has no sources.");
            if (sources.Count > storage.MaxReceiptSources) throw new InvalidOperationException("Queued receipt has too many sources.");
            documentIds = sources.Where(x => x.PurchaseDocumentId.HasValue).Select(x => x.PurchaseDocumentId!.Value).ToHashSet();
            await SetDocumentStatusesAsync(documentIds, "processing", ct);

            var categories = await db.Categories.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == job.FullWorthSpaceId && !x.IsArchived)
                .ToListAsync(ct);
            var categoryPaths = BuildCategoryPaths(categories);
            var categoryMap = BuildCategoryMap(categories, categoryPaths);

            await jobs.SetStageAsync(job.Id, "connecting", null, ct);
            var codexInput = await BuildCodexInputAsync(sources, ct);
            await jobs.SetStageAsync(job.Id, "analyzing", "gpt", ct);
            var gpt = await codex.TryScanAsync(
                job.UserId,
                job.FullWorthSpaceId,
                codexInput.Files,
                codexInput.Sources,
                categoryPaths.Values.ToList(),
                ct);

            if (gpt?.Result is not null)
            {
                await jobs.SetStageAsync(job.Id, "structuring", "gpt", ct);
                var prepared = BuildGptExtraction(gpt, purchase.Currency, categoryMap, sources.Count);
                await PersistWarningsAsync(job.Id, prepared.Warnings, ct);
                await jobs.SetStageAsync(job.Id, "saving", "gpt", ct);
                await ApplyAndPersistProvenanceAsync(job, prepared, sources, ct);
                await RecordSetExtractionRunsAsync(documentIds, "codex", "completed", JsonSerializer.Serialize(gpt.Result), null, ct);
                await SetDocumentStatusesAsync(documentIds, "review", ct);
                await jobs.CompleteAsync(job.Id, "gpt", ct);
                return;
            }

            // GPT unavailability is a normal fallback. Keep an auditable empty attempt without logging
            // receipt text or persisting bridge diagnostics into application logs.
            await RecordSetExtractionRunsAsync(documentIds, "codex", "empty", null, null, ct);
            await jobs.SetStageAsync(job.Id, "ocr", extraction.ActiveProvider, ct);

            var local = await ExtractLocallyAsync(sources, purchase.Currency, ct);
            var localPrepared = BuildLocalExtraction(local, purchase.Currency, categoryMap, sources.Count);
            if (localPrepared is not null)
            {
                await PersistWarningsAsync(job.Id, localPrepared.Warnings, ct);
                await jobs.SetStageAsync(job.Id, "saving", extraction.ActiveProvider, ct);
                await ApplyAndPersistProvenanceAsync(job, localPrepared, sources, ct);
                await RecordLocalRunsAsync(documentIds, local, extraction.ActiveProvider, ct);
                await SetDocumentStatusesAsync(documentIds, "review", ct);
                await jobs.CompleteAsync(job.Id, string.IsNullOrWhiteSpace(extraction.ActiveProvider) ? "none" : extraction.ActiveProvider, ct);
                return;
            }

            await PersistWarningsAsync(job.Id,
                sources.Count > 1 ? ["Keine Quelle lieferte zuverlässig strukturierbare Artikeldaten; manuelle Prüfung erforderlich."] : [], ct);
            await RecordLocalRunsAsync(documentIds, local, extraction.ActiveProvider, ct);
            await SetDocumentStatusesAsync(documentIds, "review", ct);
            await jobs.SetPurchaseStatusAsync(job.PurchaseId, "captured", ct);
            await jobs.CompleteAsync(job.Id, string.IsNullOrWhiteSpace(extraction.ActiveProvider) ? "none" : extraction.ActiveProvider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stale-lease recovery returns an interrupted processing job to queued. The next run replaces
            // items atomically, so retrying the whole set cannot duplicate purchase lines.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Receipt scan job {JobId} failed", job.Id);
            try { await RecordSetExtractionRunsAsync(documentIds, "queue", "failed", null, "Receipt scan processing failed.", CancellationToken.None); } catch { }
            try { await SetDocumentStatusesAsync(documentIds, "failed", CancellationToken.None); } catch { }
            try { await jobs.SetPurchaseStatusAsync(job.PurchaseId, "captured", CancellationToken.None); } catch { }
            try { await jobs.FailAsync(job.Id, "Receipt scan processing failed.", CancellationToken.None); } catch { }
        }
    }

    private async Task<List<ReceiptScanSourceRow>> EnsureSourcesAndDocumentsAsync(Purchase purchase, ReceiptScanJobRow job, CancellationToken ct)
    {
        var sources = await jobs.ListSourcesAsync(job.Id, ct);
        if (sources.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(purchase.ReceiptImagePath)) return [];
            var path = purchase.ReceiptImagePath;
            var absolute = SafeAbsolutePath(path);
            if (!File.Exists(absolute)) throw new FileNotFoundException("Queued receipt file is missing.", absolute);
            var mime = string.IsNullOrWhiteSpace(job.ContentType) ? ContentTypeFromPath(path) : job.ContentType;
            sources =
            [
                new ReceiptScanSourceRow
                {
                    Id = StableGuid($"{job.Id:N}:legacy-source"),
                    ReceiptScanJobId = job.Id,
                    SortOrder = 0,
                    SourceType = string.Equals(mime, "application/pdf", StringComparison.OrdinalIgnoreCase) ? "pdf_page" : "image",
                    OriginalFileName = string.IsNullOrWhiteSpace(job.FileName) ? Path.GetFileName(path) : job.FileName,
                    MimeType = mime,
                    StoragePath = path,
                    PageNumber = string.Equals(mime, "application/pdf", StringComparison.OrdinalIgnoreCase) ? 1 : null,
                    Fingerprint = $"legacy:{job.Id:N}",
                    SizeBytes = new FileInfo(absolute).Length,
                    CreatedAt = job.CreatedAt,
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            ];
            await jobs.CreateSourcesAsync(sources, ct);
        }

        // Backfill PurchaseDocument references for jobs created by the first queue release. Group by
        // physical storage path so a multi-page PDF always has one document and many logical sources.
        foreach (var group in sources.GroupBy(x => x.StoragePath, StringComparer.Ordinal).ToList())
        {
            var absolute = SafeAbsolutePath(group.Key);
            if (!File.Exists(absolute)) throw new FileNotFoundException("Queued receipt source is missing.", absolute);
            var documentId = group.Select(x => x.PurchaseDocumentId).FirstOrDefault(x => x.HasValue);
            PurchaseDocument? document = documentId.HasValue
                ? await db.PurchaseDocuments.SingleOrDefaultAsync(x => x.Id == documentId.Value, ct)
                : await db.PurchaseDocuments.SingleOrDefaultAsync(x => x.PurchaseId == purchase.Id && x.StoragePath == group.Key, ct);

            if (document is null)
            {
                var bytes = await File.ReadAllBytesAsync(absolute, ct);
                var first = group.OrderBy(x => x.SortOrder).First();
                document = new PurchaseDocument
                {
                    PurchaseId = purchase.Id,
                    DocumentType = "receipt",
                    OriginalFileName = Cap(first.OriginalFileName, 500),
                    MediaType = Cap(first.MimeType, 150),
                    StoragePath = group.Key,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    SizeBytes = bytes.LongLength,
                    Status = "uploaded",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.PurchaseDocuments.Add(document);
                await db.SaveChangesAsync(ct);
            }

            foreach (var source in group.Where(x => x.PurchaseDocumentId != document.Id))
            {
                source.PurchaseDocumentId = document.Id;
                if (source.Fingerprint.StartsWith("legacy:", StringComparison.Ordinal))
                    source.Fingerprint = source.PageNumber.HasValue ? $"{document.Sha256}:page:{source.PageNumber}" : document.Sha256;
                await jobs.UpdateSourceAsync(source, ct);
            }
        }

        sources = await jobs.ListSourcesAsync(job.Id, ct);
        sources = await ExpandPdfPagesAsync(job.Id, sources, ct);
        return sources.OrderBy(x => x.SortOrder).ToList();
    }

    private async Task<List<ReceiptScanSourceRow>> ExpandPdfPagesAsync(Guid jobId, List<ReceiptScanSourceRow> sources, CancellationToken ct)
    {
        foreach (var physical in sources
                     .Where(x => string.Equals(x.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(x.StoragePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                     .GroupBy(x => x.StoragePath, StringComparer.Ordinal)
                     .ToList())
        {
            var absolute = SafeAbsolutePath(physical.Key);
            var pageCount = await ReceiptPdfRasterizer.GetPageCountAsync(absolute, storage.MaxReceiptSources, ct);
            var existing = physical.OrderBy(x => x.PageNumber ?? 1).ToList();
            if (existing.Any(x => (x.PageNumber ?? 1) > pageCount))
                throw new InvalidOperationException("Stored PDF has fewer pages than the persisted receipt source set.");

            if (existing[0].PurchaseDocumentId.HasValue)
            {
                var document = await db.PurchaseDocuments.SingleAsync(x => x.Id == existing[0].PurchaseDocumentId.Value, ct);
                if (document.PageCount != pageCount)
                {
                    document.PageCount = pageCount;
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }

            var existingPages = existing.Select(x => x.PageNumber ?? 1).ToHashSet();
            var missingPages = Enumerable.Range(1, pageCount).Where(page => !existingPages.Contains(page)).ToList();
            if (missingPages.Count == 0) continue;
            if (sources.Count + missingPages.Count > storage.MaxReceiptSources)
                throw new InvalidOperationException("Expanded receipt PDF exceeds the scan source limit.");

            var root = existing.First();
            var additions = missingPages.Select((page, index) => new ReceiptScanSourceRow
            {
                Id = DerivedSourceId(root.Id, page),
                ReceiptScanJobId = jobId,
                PurchaseDocumentId = root.PurchaseDocumentId,
                SortOrder = 1_500_000 + index,
                SourceType = "pdf_page",
                OriginalFileName = root.OriginalFileName,
                MimeType = "application/pdf",
                StoragePath = root.StoragePath,
                PageNumber = page,
                Fingerprint = $"{FingerprintRoot(root.Fingerprint)}:page:{page}",
                SizeBytes = root.SizeBytes,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }).ToList();
            await jobs.CreateSourcesAsync(additions, ct);

            var ordered = sources.OrderBy(x => x.SortOrder).ToList();
            var groupIds = existing.Select(x => x.Id).ToHashSet();
            var insertionIndex = ordered.FindIndex(x => groupIds.Contains(x.Id));
            if (insertionIndex < 0) insertionIndex = ordered.Count;
            var outside = ordered.Where(x => !groupIds.Contains(x.Id)).Select(x => x.Id).ToList();
            var completeGroup = existing.Concat(additions).OrderBy(x => x.PageNumber ?? 1).Select(x => x.Id).ToList();
            outside.InsertRange(Math.Min(insertionIndex, outside.Count), completeGroup);
            await jobs.ReorderSourcesAsync(jobId, outside, ct);
            sources = await jobs.ListSourcesAsync(jobId, ct);
        }
        return sources;
    }

    private async Task<CodexInputSet> BuildCodexInputAsync(IReadOnlyList<ReceiptScanSourceRow> orderedSources, CancellationToken ct)
    {
        var files = new List<CodexReceiptInput>();
        var sourceDtos = new List<CodexReceiptSource>();
        var fileIds = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var (source, index) in orderedSources.OrderBy(x => x.SortOrder).Select((source, index) => (source, index)))
        {
            if (!fileIds.TryGetValue(source.StoragePath, out var fileId))
            {
                fileId = source.PurchaseDocumentId ?? StableGuid(source.StoragePath);
                fileIds[source.StoragePath] = fileId;
                var absolute = SafeAbsolutePath(source.StoragePath);
                if (!File.Exists(absolute)) throw new FileNotFoundException("Queued receipt source is missing.", absolute);
                files.Add(new CodexReceiptInput(fileId, source.OriginalFileName, source.MimeType, await File.ReadAllBytesAsync(absolute, ct)));
            }
            sourceDtos.Add(new CodexReceiptSource(source.Id, fileId, index, source.PageNumber));
        }
        return new(files, sourceDtos);
    }

    private async Task<List<LocalSourceExtraction>> ExtractLocallyAsync(
        IReadOnlyList<ReceiptScanSourceRow> sources,
        string fallbackCurrency,
        CancellationToken ct)
    {
        var results = new List<LocalSourceExtraction>();
        foreach (var (source, index) in sources.OrderBy(x => x.SortOrder).Select((source, index) => (source, index)))
        {
            byte[] content;
            string contentType;
            string fileName;
            var absolute = SafeAbsolutePath(source.StoragePath);
            if (source.SourceType == "pdf_page")
            {
                content = await ReceiptPdfRasterizer.RenderPageAsync(absolute, source.PageNumber ?? 1, storage.MaxReceiptSources, ct);
                contentType = "image/png";
                fileName = $"{Path.GetFileNameWithoutExtension(source.OriginalFileName)}-page-{source.PageNumber ?? 1}.png";
            }
            else
            {
                content = await File.ReadAllBytesAsync(absolute, ct);
                contentType = source.MimeType;
                fileName = source.OriginalFileName;
            }

            var result = await extraction.ExtractAsync(new ReceiptExtractionRequest(content, contentType, fileName, fallbackCurrency), ct);
            results.Add(new(index, source, result));
        }
        return results;
    }

    private PreparedExtraction BuildGptExtraction(
        CodexReceiptScanEnvelope envelope,
        string fallbackCurrency,
        IReadOnlyDictionary<string, Guid> categoryMap,
        int sourceCount)
    {
        var result = envelope.Result!;
        var currency = NormalizeCurrency(result.Receipt.Currency, fallbackCurrency);
        var warnings = result.Warnings.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        var lines = new List<LineDraft>();
        for (var index = 0; index < result.Items.Count; index++)
        {
            var item = result.Items[index];
            if (string.IsNullOrWhiteSpace(item.Name) && string.IsNullOrWhiteSpace(item.RawName)) continue;
            var quantity = item.Quantity is > 0m ? item.Quantity.Value : 1m;
            var deposit = item.Deposit is > 0m ? item.Deposit.Value : 0m;
            var effectiveTotal = PurchaseArticleCalculator.RoundMoney(item.TotalPrice ?? 0m, currency);
            var sourceIndexes = item.SourceIndexes.Where(x => x >= 0 && x < sourceCount).Distinct().ToHashSet();
            if (sourceIndexes.Count == 0 && sourceCount == 1) sourceIndexes.Add(0);
            if (item.SourceIndexes.Any(x => x < 0 || x >= sourceCount))
                warnings.Add($"Artikel '{Trim(item.Name) ?? Trim(item.RawName) ?? "?"}' enthielt eine ungültige Quellenreferenz und muss geprüft werden.");

            var write = new PurchaseItemWrite(
                CategoryId: CategoryId(categoryMap, item.CategorySuggestion),
                Name: (item.Name ?? item.RawName ?? "Artikel").Trim(),
                Brand: Trim(item.Brand),
                Sku: null,
                Asin: null,
                Quantity: quantity,
                UnitPrice: item.UnitPrice,
                TotalPrice: effectiveTotal,
                Currency: currency,
                Notes: ItemNotes(item),
                RawName: Trim(item.RawName),
                QuantityUnit: NormalizeUnit(item.Unit),
                DepositAmount: deposit > 0m ? deposit : null,
                LineType: "product",
                ExtractionConfidence: Math.Clamp(item.Confidence, 0m, 1m),
                SortOrder: index,
                OriginalUnitPrice: item.OriginalUnitPrice,
                DiscountAmount: item.DiscountAmount is > 0m ? item.DiscountAmount : null,
                DiscountLabel: Trim(item.DiscountLabel));
            lines.Add(new(index, write, sourceIndexes, Signature(write)));
        }

        DedupeAdjacentOverlaps(lines, sourceCount, warnings);
        var active = NormalizeLineOrder(lines);
        var discounts = BuildGptDiscounts(result.Discounts, lines, active, sourceCount, warnings);
        var basketDiscountTotal = discounts.Where(x => !x.ItemIndex.HasValue).Sum(x => x.Amount);
        var depositTotal = result.Totals.Deposits ?? active.Sum(x => Math.Max(0m, x.Item.DepositAmount ?? 0m));
        var rounding = result.Totals.Rounding ?? 0m;
        var fallbackTotal = PurchaseArticleCalculator.RoundMoney(
            active.Sum(x => x.Item.TotalPrice) - basketDiscountTotal + Math.Max(0m, depositTotal) + rounding,
            currency);
        var total = result.Totals.Total ?? fallbackTotal;
        var notes = BuildNotes(
            $"GPT/Codex scan set · {sourceCount} Quelle(n) · confidence {result.Confidence:0.000}",
            result.Totals.Discounts,
            result.Totals.Subtotal,
            result.Totals.Deposits,
            result.Totals.Tax,
            currency,
            warnings.Count == 0 ? null : $"Scan-Hinweise: {string.Join(" | ", warnings)}");
        var request = new PurchaseExtractionRequest(
            Merchant: Trim(result.Merchant.Name) ?? "Unbekannt",
            PurchaseDate: ParseDate(result.Receipt.Date),
            TotalAmount: total,
            Currency: currency,
            Items: active.Select(x => x.Item).ToList(),
            SourceReference: string.IsNullOrWhiteSpace(envelope.RequestId) ? "codex:server-queue-set" : $"codex:{envelope.RequestId}",
            Notes: notes,
            PurchaseTime: ParseTime(result.Receipt.Time),
            SubtotalAmount: result.Totals.Subtotal,
            DiscountAmount: result.Totals.Discounts ?? discounts.Sum(x => x.Amount),
            DepositAmount: result.Totals.Deposits,
            TaxAmount: result.Totals.Tax,
            ReceiptNumber: Trim(result.Receipt.ReceiptNumber),
            PaymentMethodText: Trim(result.Payment.Method),
            RoundingAmount: result.Totals.Rounding,
            Discounts: discounts,
            DiscountSource: "codex",
            AmountsAreCanonical: true);
        return new(request, active.Select(x => (IReadOnlySet<int>)x.SourceIndexes).ToList(), warnings);
    }

    private static List<PurchaseDiscountImport> BuildGptDiscounts(
        IReadOnlyList<CodexReceiptDiscount> extracted,
        IReadOnlyList<LineDraft> allLines,
        IReadOnlyList<LineDraft> active,
        int sourceCount,
        List<string> warnings)
    {
        var result = new List<PurchaseDiscountImport>();
        foreach (var discount in extracted)
        {
            if (discount.Amount <= 0m) continue;
            if (discount.SourceIndexes.Any(x => x < 0 || x >= sourceCount))
                warnings.Add($"Rabatt '{Trim(discount.Label) ?? Trim(discount.RawText) ?? "?"}' enthielt eine ungültige Quellenreferenz und muss geprüft werden.");

            int? persistedItemIndex = null;
            if (discount.ItemIndex.HasValue)
            {
                persistedItemIndex = ResolveActiveItemIndex(allLines, active, discount.ItemIndex.Value);
                if (!persistedItemIndex.HasValue)
                    warnings.Add($"Artikelzuordnung des Rabatts '{Trim(discount.Label) ?? Trim(discount.RawText) ?? "?"}' konnte nach der Beleg-Deduplizierung nicht sicher erhalten werden; der Rabatt bleibt zur Prüfung auf Warenkorbebene.");
            }

            result.Add(new PurchaseDiscountImport(
                PurchaseItemId: null,
                Type: NormalizeDiscountType(discount.Type),
                Label: Trim(discount.Label),
                Amount: discount.Amount,
                Percentage: discount.Percentage,
                CouponCode: Trim(discount.CouponCode),
                RawText: Trim(discount.RawText),
                Source: "codex",
                Confidence: Math.Clamp(discount.Confidence, 0m, 1m),
                ItemIndex: persistedItemIndex));
        }
        return result;
    }

    private static int? ResolveActiveItemIndex(
        IReadOnlyList<LineDraft> allLines,
        IReadOnlyList<LineDraft> active,
        int originalItemIndex)
    {
        var current = allLines.FirstOrDefault(x => x.OriginalOrder == originalItemIndex);
        var seen = new HashSet<int>();
        while (current is not null && current.Removed && current.MergedIntoOriginalOrder.HasValue && seen.Add(current.OriginalOrder))
            current = allLines.FirstOrDefault(x => x.OriginalOrder == current.MergedIntoOriginalOrder.Value);
        if (current is null || current.Removed) return null;
        for (var index = 0; index < active.Count; index++)
            if (ReferenceEquals(active[index], current) || active[index].OriginalOrder == current.OriginalOrder) return index;
        return null;
    }

    private PreparedExtraction? BuildLocalExtraction(
        IReadOnlyList<LocalSourceExtraction> sourceResults,
        string fallbackCurrency,
        IReadOnlyDictionary<string, Guid> categoryMap,
        int sourceCount)
    {
        if (sourceResults.Count == 0 || !sourceResults.Any(x => HasUsefulData(x.Result))) return null;
        var currency = NormalizeCurrency(sourceResults.Select(x => x.Result.Currency).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)), fallbackCurrency);
        var warnings = new List<string>();
        var lines = new List<LineDraft>();
        var sourceItemOrders = new Dictionary<(int SourceIndex, int ItemIndex), int>();
        var order = 0;
        foreach (var source in sourceResults.OrderBy(x => x.SourceIndex))
        {
            if (!HasUsefulData(source.Result) && sourceCount > 1)
                warnings.Add($"Quelle {source.SourceIndex + 1} lieferte keine strukturierten OCR-Daten und sollte geprüft werden.");
            for (var localIndex = 0; localIndex < source.Result.Items.Count; localIndex++)
            {
                var item = source.Result.Items[localIndex];
                var quantity = item.Quantity is > 0m ? item.Quantity.Value : 1m;
                var write = new PurchaseItemWrite(
                    CategoryId: CategoryId(categoryMap, item.CategoryHint),
                    Name: item.Name,
                    Brand: null,
                    Sku: null,
                    Asin: null,
                    Quantity: quantity,
                    UnitPrice: item.UnitPrice,
                    TotalPrice: PurchaseArticleCalculator.RoundMoney(item.TotalPrice ?? 0m, currency),
                    Currency: currency,
                    Notes: null,
                    RawName: item.Name,
                    QuantityUnit: NormalizeUnit(item.QuantityUnit),
                    DepositAmount: item.DepositAmount is > 0m ? item.DepositAmount : null,
                    LineType: NormalizeLocalLineType(item.LineType),
                    ExtractionConfidence: Math.Clamp(item.Confidence, 0m, 1m),
                    SortOrder: order,
                    OriginalUnitPrice: item.OriginalUnitPrice,
                    DiscountAmount: item.DiscountAmount is > 0m ? item.DiscountAmount : null,
                    DiscountLabel: Trim(item.DiscountLabel));
                sourceItemOrders[(source.SourceIndex, localIndex)] = order;
                lines.Add(new(order++, write, [source.SourceIndex], Signature(write)));
            }
        }

        DedupeAdjacentOverlaps(lines, sourceCount, warnings);
        var active = NormalizeLineOrder(lines);

        var orderedResults = sourceResults.OrderBy(x => x.SourceIndex).Select(x => x.Result).ToList();
        var lastWithTotal = orderedResults.Select(x => x.Total).LastOrDefault(x => x.HasValue);
        var lastSubtotal = orderedResults.Select(x => x.Subtotal).LastOrDefault(x => x.HasValue);
        var lastDiscount = orderedResults.Select(x => x.Discounts).LastOrDefault(x => x.HasValue);
        var lastDeposit = orderedResults.Select(x => x.Deposits).LastOrDefault(x => x.HasValue);
        var lastTax = orderedResults.Select(x => x.Taxes).LastOrDefault(x => x.HasValue);
        var lastRounding = orderedResults.Select(x => x.Rounding).LastOrDefault(x => x.HasValue);
        var lastTip = orderedResults.Select(x => x.Tip).LastOrDefault(x => x.HasValue);
        var lastShipping = orderedResults.Select(x => x.Shipping).LastOrDefault(x => x.HasValue);
        var lastFees = orderedResults.Select(x => x.Fees).LastOrDefault(x => x.HasValue);

        var localDiscounts = BuildLocalDiscounts(
            sourceResults,
            sourceItemOrders,
            lines,
            active,
            lastDiscount,
            currency,
            warnings);
        var structuredDiscountTotal = localDiscounts.Sum(x => x.Amount);
        var declaredDiscount = Math.Max(lastDiscount ?? 0m, structuredDiscountTotal);
        var basketDiscount = localDiscounts.Where(x => !x.ItemIndex.HasValue).Sum(x => x.Amount);
        var depositTotal = Math.Max(0m, lastDeposit ?? active.Sum(x => Math.Max(0m, x.Item.DepositAmount ?? 0m)));
        var rounding = lastRounding ?? 0m;
        var additionalCharges = Math.Max(0m, lastTip ?? 0m) + Math.Max(0m, lastShipping ?? 0m) + Math.Max(0m, lastFees ?? 0m);
        var fallbackTotal = PurchaseArticleCalculator.RoundMoney(
            active.Sum(x => x.Item.TotalPrice) - basketDiscount + depositTotal + additionalCharges + rounding,
            currency);
        var total = lastWithTotal ?? fallbackTotal;
        var tolerance = PurchaseArticleCalculator.Tolerance(currency);
        if (lastWithTotal.HasValue && Math.Abs(PurchaseArticleCalculator.RoundMoney(lastWithTotal.Value - fallbackTotal, currency)) > tolerance)
            warnings.Add($"Artikelsumme und Belegsumme unterscheiden sich nach kanonischen Rabatten/Pfand/Zusatzkosten noch um {PurchaseArticleCalculator.RoundMoney(lastWithTotal.Value - fallbackTotal, currency):0.###} {currency}; keine unsichere Position wurde automatisch erfunden.");

        var merchant = orderedResults.Select(x => Trim(x.Merchant)).FirstOrDefault(x => x is not null) ?? "Unbekannt";
        var date = orderedResults.Select(x => x.PurchaseDate).FirstOrDefault(x => x.HasValue);
        var confidenceRows = orderedResults.Where(HasUsefulData).Select(x => x.Confidence).ToList();
        var confidence = confidenceRows.Count == 0 ? 0m : confidenceRows.Average();
        var notes = BuildNotes(
            $"OCR scan set · {sourceCount} Quelle(n) · {extraction.ActiveProvider} · confidence {confidence:0.000}",
            declaredDiscount > 0m ? declaredDiscount : null,
            lastSubtotal,
            depositTotal > 0m ? depositTotal : null,
            lastTax,
            currency,
            warnings.Count == 0 ? null : $"Scan-Hinweise: {string.Join(" | ", warnings)}");
        var sourceName = string.IsNullOrWhiteSpace(extraction.ActiveProvider) || string.Equals(extraction.ActiveProvider, "none", StringComparison.OrdinalIgnoreCase)
            ? "ocr"
            : extraction.ActiveProvider.Trim().ToLowerInvariant();
        var request = new PurchaseExtractionRequest(
            Merchant: merchant,
            PurchaseDate: date,
            TotalAmount: total,
            Currency: currency,
            Items: active.Select(x => x.Item).ToList(),
            SourceReference: $"ocr-set:{sourceName}",
            Notes: notes,
            SubtotalAmount: lastSubtotal,
            DiscountAmount: declaredDiscount > 0m ? declaredDiscount : null,
            DepositAmount: depositTotal > 0m ? depositTotal : null,
            TaxAmount: lastTax,
            TipAmount: lastTip,
            ShippingAmount: lastShipping,
            FeeAmount: lastFees,
            RoundingAmount: lastRounding,
            Discounts: localDiscounts,
            DiscountSource: sourceName,
            AmountsAreCanonical: true);
        return new(request, active.Select(x => (IReadOnlySet<int>)x.SourceIndexes).ToList(), warnings);
    }

    private static List<PurchaseDiscountImport> BuildLocalDiscounts(
        IReadOnlyList<LocalSourceExtraction> sourceResults,
        IReadOnlyDictionary<(int SourceIndex, int ItemIndex), int> sourceItemOrders,
        IReadOnlyList<LineDraft> allLines,
        IReadOnlyList<LineDraft> active,
        decimal? aggregateDiscount,
        string currency,
        List<string> warnings)
    {
        var result = new List<PurchaseDiscountImport>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceResults.OrderBy(x => x.SourceIndex))
        {
            foreach (var discount in source.Result.StructuredDiscounts ?? [])
            {
                if (discount.Amount <= 0m) continue;
                int? persistedItemIndex = null;
                if (discount.ItemIndex.HasValue)
                {
                    if (!sourceItemOrders.TryGetValue((source.SourceIndex, discount.ItemIndex.Value), out var originalOrder))
                    {
                        warnings.Add($"Artikelzuordnung des OCR-Rabatts '{Trim(discount.Label) ?? Trim(discount.RawText) ?? "?"}' liegt außerhalb der erkannten Artikel und bleibt auf Warenkorbebene.");
                    }
                    else
                    {
                        persistedItemIndex = ResolveActiveItemIndex(allLines, active, originalOrder);
                        if (!persistedItemIndex.HasValue)
                            warnings.Add($"Artikelzuordnung des OCR-Rabatts '{Trim(discount.Label) ?? Trim(discount.RawText) ?? "?"}' konnte nach der Beleg-Deduplizierung nicht sicher erhalten werden; der Rabatt bleibt auf Warenkorbebene.");
                    }
                }

                var amount = PurchaseArticleCalculator.RoundMoney(Math.Abs(discount.Amount), currency);
                var type = NormalizeDiscountType(discount.Type);
                var label = Trim(discount.Label);
                var raw = Trim(discount.RawText);
                var key = $"{persistedItemIndex}|{type}|{label}|{amount}|{discount.Percentage}|{Trim(discount.CouponCode)}|{raw}";
                if (!keys.Add(key)) continue;
                result.Add(new PurchaseDiscountImport(
                    PurchaseItemId: null,
                    Type: type,
                    Label: label,
                    Amount: amount,
                    Percentage: discount.Percentage,
                    CouponCode: Trim(discount.CouponCode),
                    RawText: raw,
                    Source: "ocr",
                    Confidence: Math.Clamp(discount.Confidence, 0m, 1m),
                    ItemIndex: persistedItemIndex));
            }
        }

        for (var index = 0; index < active.Count; index++)
        {
            var item = active[index].Item;
            var amount = Math.Max(0m, item.DiscountAmount ?? 0m);
            if (amount <= 0m || result.Any(x => x.ItemIndex == index)) continue;
            amount = PurchaseArticleCalculator.RoundMoney(amount, currency);
            var label = Trim(item.DiscountLabel) ?? "OCR item price reduction";
            var key = $"{index}|price_reduction|{label}|{amount}";
            if (!keys.Add(key)) continue;
            result.Add(new PurchaseDiscountImport(
                PurchaseItemId: null,
                Type: "price_reduction",
                Label: label,
                Amount: amount,
                Percentage: null,
                CouponCode: null,
                RawText: item.RawName,
                Source: "ocr",
                Confidence: item.ExtractionConfidence,
                ItemIndex: index));
        }

        var recognized = result.Sum(x => x.Amount);
        var aggregate = Math.Max(0m, aggregateDiscount ?? 0m);
        var tolerance = PurchaseArticleCalculator.Tolerance(currency);
        if (aggregate > recognized + tolerance)
        {
            result.Add(new PurchaseDiscountImport(
                PurchaseItemId: null,
                Type: "other",
                Label: "OCR receipt discount remainder",
                Amount: PurchaseArticleCalculator.RoundMoney(aggregate - recognized, currency),
                Percentage: null,
                CouponCode: null,
                RawText: null,
                Source: "ocr",
                Confidence: null,
                ItemIndex: null));
        }
        else if (recognized > aggregate + tolerance && aggregateDiscount.HasValue)
        {
            warnings.Add($"Strukturierte OCR-Rabatte ({recognized:0.###} {currency}) übersteigen den erkannten Gesamt-Rabatt ({aggregate:0.###} {currency}); strukturierte Zeilen bleiben erhalten und der Kauf muss geprüft werden.");
        }
        return result;
    }

    private static string NormalizeLocalLineType(string? value)
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

    private static void DedupeAdjacentOverlaps(List<LineDraft> lines, int sourceCount, List<string> warnings)
    {
        for (var sourceIndex = 0; sourceIndex < sourceCount - 1; sourceIndex++)
        {
            var left = lines.Where(x => !x.Removed && x.SourceIndexes.Contains(sourceIndex) && !x.SourceIndexes.Contains(sourceIndex + 1))
                .OrderBy(x => x.OriginalOrder).ToList();
            var right = lines.Where(x => !x.Removed && x.SourceIndexes.Contains(sourceIndex + 1) && !x.SourceIndexes.Contains(sourceIndex))
                .OrderBy(x => x.OriginalOrder).ToList();
            var max = Math.Min(12, Math.Min(left.Count, right.Count));
            var match = 0;
            for (var length = max; length >= 1; length--)
            {
                var identical = true;
                for (var offset = 0; offset < length; offset++)
                {
                    if (left[left.Count - length + offset].Signature == right[offset].Signature) continue;
                    identical = false;
                    break;
                }
                if (!identical) continue;
                match = length;
                break;
            }

            if (match >= 2)
            {
                for (var offset = 0; offset < match; offset++)
                {
                    var keep = left[left.Count - match + offset];
                    var duplicate = right[offset];
                    keep.SourceIndexes.UnionWith(duplicate.SourceIndexes);
                    duplicate.MergedIntoOriginalOrder = keep.OriginalOrder;
                    duplicate.Removed = true;
                }
            }
            else if (match == 1)
            {
                warnings.Add($"Mögliche Foto-Überlappung zwischen Quelle {sourceIndex + 1} und {sourceIndex + 2}: eine einzelne identische Position wurde bewusst nicht automatisch entfernt.");
            }
        }
    }

    private static List<LineDraft> NormalizeLineOrder(List<LineDraft> lines)
    {
        var active = lines.Where(x => !x.Removed).OrderBy(x => x.OriginalOrder).ToList();
        for (var index = 0; index < active.Count; index++) active[index].Item = active[index].Item with { SortOrder = index };
        return active;
    }

    private async Task ApplyAndPersistProvenanceAsync(
        ReceiptScanJobRow job,
        PreparedExtraction prepared,
        IReadOnlyList<ReceiptScanSourceRow> sources,
        CancellationToken ct)
    {
        var applied = await capture.ApplyExtractionAsync(job.UserId, job.FullWorthSpaceId, job.PurchaseId, prepared.Request, ct);
        if (applied.Result != PurchaseMutationResult.Success)
            throw new InvalidOperationException($"Receipt extraction could not be applied: {applied.Result}.");

        var items = await db.PurchaseItems.AsNoTracking().Where(x => x.PurchaseId == job.PurchaseId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
        if (items.Count != prepared.ItemSourceIndexes.Count)
            throw new InvalidOperationException("Persisted receipt items no longer match extraction provenance.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM "ReceiptScanItemSources"
            WHERE "PurchaseItemId" IN (SELECT "Id" FROM "PurchaseItems" WHERE "PurchaseId" = {job.PurchaseId})
            """, ct);
        for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            foreach (var sourceIndex in prepared.ItemSourceIndexes[itemIndex].Where(index => index >= 0 && index < sources.Count).Distinct())
            {
                var sourceId = sources[sourceIndex].Id;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "ReceiptScanItemSources" ("PurchaseItemId", "ReceiptScanSourceId", "CreatedAt")
                    VALUES ({items[itemIndex]}, {sourceId}, {DateTimeOffset.UtcNow})
                    ON CONFLICT ("PurchaseItemId", "ReceiptScanSourceId") DO NOTHING
                    """, ct);
            }
        }
        await transaction.CommitAsync(ct);
    }

    private async Task SetDocumentStatusesAsync(IEnumerable<Guid> documentIds, string status, CancellationToken ct)
    {
        var ids = documentIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        var documents = await db.PurchaseDocuments.Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var document in documents)
        {
            document.Status = status;
            document.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordSetExtractionRunsAsync(
        IEnumerable<Guid> documentIds,
        string provider,
        string status,
        string? normalizedResultJson,
        string? safeError,
        CancellationToken ct)
    {
        var ids = documentIds.Distinct().OrderBy(x => x).ToList();
        if (ids.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < ids.Count; index++)
        {
            db.PurchaseExtractionRuns.Add(new PurchaseExtractionRun
            {
                PurchaseDocumentId = ids[index],
                Provider = Cap(string.IsNullOrWhiteSpace(provider) ? "none" : provider, 64),
                Status = Cap(status, 32),
                StartedAt = now,
                CompletedAt = now,
                ErrorCode = safeError is null ? null : "queue_processing_failed",
                ErrorMessageSafe = safeError,
                // The normalized full-set result is stored once; sibling documents still get a run row
                // proving they participated without duplicating potentially large JSON N times.
                NormalizedResultJson = index == 0 ? normalizedResultJson : null,
                CreatedAt = now
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordLocalRunsAsync(
        IEnumerable<Guid> documentIds,
        IReadOnlyList<LocalSourceExtraction> local,
        string provider,
        CancellationToken ct)
    {
        var byDocument = local.Where(x => x.Source.PurchaseDocumentId.HasValue)
            .GroupBy(x => x.Source.PurchaseDocumentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(x => new { x.SourceIndex, x.Source.PageNumber, x.Result }).ToList());
        var ids = documentIds.Distinct().ToList();
        var now = DateTimeOffset.UtcNow;
        foreach (var documentId in ids)
        {
            byDocument.TryGetValue(documentId, out var result);
            db.PurchaseExtractionRuns.Add(new PurchaseExtractionRun
            {
                PurchaseDocumentId = documentId,
                Provider = Cap(string.IsNullOrWhiteSpace(provider) ? "none" : provider, 64),
                Status = result is not null && result.Any(x => HasUsefulData(x.Result)) ? "completed" : "empty",
                StartedAt = now,
                CompletedAt = now,
                NormalizedResultJson = result is null ? null : JsonSerializer.Serialize(result),
                CreatedAt = now
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private Task PersistWarningsAsync(Guid jobId, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        var distinct = warnings.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().Take(50).ToList();
        return jobs.SetWarningsAsync(jobId, distinct.Count == 0 ? null : JsonSerializer.Serialize(distinct), ct);
    }

    private string SafeAbsolutePath(string relative)
    {
        var root = Path.GetFullPath(storage.RootPath);
        var candidate = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Receipt path escaped configured storage root.");
        return candidate;
    }

    private static string Signature(PurchaseItemWrite item)
    {
        var name = new string((item.RawName ?? item.Name).Normalize(NormalizationForm.FormKC)
            .ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        var quantity = Math.Round(item.Quantity, 3, MidpointRounding.AwayFromZero);
        var unit = PurchaseArticleCalculator.NormalizeUnit(item.QuantityUnit);
        var total = Math.Round(item.TotalPrice, 3, MidpointRounding.AwayFromZero);
        var unitPrice = item.UnitPrice.HasValue ? Math.Round(item.UnitPrice.Value, 3, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        return $"{name}|{quantity.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{unit}|{unitPrice}|{total.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string FingerprintRoot(string fingerprint)
    {
        var marker = fingerprint.IndexOf(":page:", StringComparison.Ordinal);
        return marker > 0 ? fingerprint[..marker] : fingerprint;
    }

    private static Guid DerivedSourceId(Guid root, int page) => StableGuid($"{root:N}:page:{page}");

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var bytes = hash[..16].ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static Dictionary<Guid, string> BuildCategoryPaths(IReadOnlyList<FinanceCategory> categories)
    {
        var byId = categories.ToDictionary(x => x.Id);
        var result = new Dictionary<Guid, string>();
        foreach (var category in categories)
        {
            var names = new Stack<string>();
            var seen = new HashSet<Guid>();
            FinanceCategory? current = category;
            while (current is not null && seen.Add(current.Id))
            {
                if (!string.IsNullOrWhiteSpace(current.Name)) names.Push(current.Name.Trim());
                current = current.ParentId.HasValue && byId.TryGetValue(current.ParentId.Value, out var parent) ? parent : null;
            }
            result[category.Id] = string.Join(" > ", names);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, Guid> BuildCategoryMap(
        IReadOnlyList<FinanceCategory> categories,
        IReadOnlyDictionary<Guid, string> paths)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in paths.Where(x => !string.IsNullOrWhiteSpace(x.Value))) map[pair.Value] = pair.Key;
        foreach (var group in categories.GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(x => x.Count() == 1))
            map.TryAdd(group.Key, group.Single().Id);
        return map;
    }

    private static Guid? CategoryId(IReadOnlyDictionary<string, Guid> map, string? hint) =>
        !string.IsNullOrWhiteSpace(hint) && map.TryGetValue(hint.Trim(), out var id) ? id : null;

    private static bool HasUsefulData(ReceiptExtractionResult result) =>
        result.Merchant is not null || result.PurchaseDate is not null || result.Total is not null || result.Items.Count > 0 ||
        result.Subtotal is not null || result.Discounts is not null || result.Deposits is not null || result.StructuredDiscounts is { Count: > 0 };

    private static DateOnly? ParseDate(string? value) => DateOnly.TryParse(value, out var date) ? date : null;
    private static TimeOnly? ParseTime(string? value) => TimeOnly.TryParse(value, out var time) ? time : null;

    private static string NormalizeCurrency(string? candidate, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim().ToUpperInvariant();
        return value.Length == 3 && value.All(x => x is >= 'A' and <= 'Z') ? value : fallback;
    }

    private static string NormalizeDiscountType(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "other" : value.Trim().ToLowerInvariant();
        return PurchaseDiscountTypes.Allowed.Contains(normalized) ? normalized : "other";
    }

    private static string NormalizeUnit(string? value) => PurchaseArticleCalculator.NormalizeUnit(value);
    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? ItemNotes(CodexReceiptItem item)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.RawName) && !string.Equals(item.RawName, item.Name, StringComparison.Ordinal))
            notes.Add($"GPT raw: {item.RawName.Trim()}");
        if (item.Deposit is > 0m) notes.Add($"Pfand: {item.Deposit.Value:0.00}");
        return notes.Count == 0 ? null : string.Join(" · ", notes);
    }

    private static string BuildNotes(
        string source,
        decimal? discounts,
        decimal? subtotal,
        decimal? deposits,
        decimal? tax,
        string currency,
        string? extra)
    {
        var notes = new List<string> { source };
        if (discounts is > 0m) notes.Add($"Erkannte Rabatte: {discounts.Value:0.00} {currency}");
        if (subtotal is not null) notes.Add($"Zwischensumme: {subtotal.Value:0.00} {currency}");
        if (deposits is > 0m) notes.Add($"Pfand: {deposits.Value:0.00} {currency}");
        if (tax is not null) notes.Add($"Steuer: {tax.Value:0.00} {currency}");
        if (!string.IsNullOrWhiteSpace(extra)) notes.Add(extra);
        return string.Join("\n", notes);
    }

    private static string ContentTypeFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };

    private sealed record CodexInputSet(List<CodexReceiptInput> Files, List<CodexReceiptSource> Sources);
    private sealed record LocalSourceExtraction(int SourceIndex, ReceiptScanSourceRow Source, ReceiptExtractionResult Result);
    private sealed record PreparedExtraction(PurchaseExtractionRequest Request, IReadOnlyList<IReadOnlySet<int>> ItemSourceIndexes, IReadOnlyList<string> Warnings);

    private sealed class LineDraft(int originalOrder, PurchaseItemWrite item, HashSet<int> sourceIndexes, string signature)
    {
        public int OriginalOrder { get; } = originalOrder;
        public PurchaseItemWrite Item { get; set; } = item;
        public HashSet<int> SourceIndexes { get; } = sourceIndexes;
        public string Signature { get; } = signature;
        public int? MergedIntoOriginalOrder { get; set; }
        public bool Removed { get; set; }
    }
}
