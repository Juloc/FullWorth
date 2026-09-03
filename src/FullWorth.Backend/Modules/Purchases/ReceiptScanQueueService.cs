using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Owns the durable receipt-scan draft. A job is created once and can contain many ordered photos or
/// logical PDF pages before it is explicitly queued. Files are persisted before the browser may close;
/// the worker never depends on browser state.
/// </summary>
public sealed class ReceiptScanQueueService(
    FullWorthDbContext db,
    PurchaseAuthorizationStore authorization,
    ReceiptScanJobStore jobs,
    IOptions<PurchaseStorageOptions> storageOptions)
{
    private readonly PurchaseStorageOptions storage = storageOptions.Value;

    public async Task<ReceiptScanEnqueueOutcome> EnqueueAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        HttpRequest request,
        CancellationToken ct)
    {
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct))
            return new(false, Error: "FullWorth Space not found.");
        if (!request.HasFormContentType)
            return new(false, Error: "multipart/form-data is required.");

        var form = await request.ReadFormAsync(ct);
        var clientJobIdText = form["clientJobId"].ToString().Trim();
        var jobId = Guid.TryParse(clientJobIdText, out var parsedJobId) && parsedJobId != Guid.Empty
            ? parsedJobId
            : Guid.NewGuid();

        // Whole-set idempotency. A lost HTTP response can be retried with the exact same clientJobId;
        // returning the committed draft must never append the files again.
        var existing = await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        if (existing is not null) return new(true, existing);

        var files = ReceiptFiles(form);
        if (files.Count == 0) return new(false, Error: "at least one receipt file is required.");

        var currency = NormalizeCurrency(form["currency"].ToString());
        if (currency is null) return new(false, Error: "currency must be a three-letter code.");

        var sourceIds = ParseSourceIds(form, files.Count);
        if (sourceIds.Error is not null) return new(false, Error: sourceIds.Error);

        var purchaseId = Guid.NewGuid();
        List<PreparedReceiptFile> prepared;
        try
        {
            prepared = await PrepareFilesAsync(
                files,
                sourceIds.Ids,
                purchaseId,
                jobId,
                fullWorthSpaceId,
                userId,
                startingSortOrder: 0,
                ct);
        }
        catch (ReceiptScanValidationException exception)
        {
            return new(false, Error: exception.Message);
        }

        var allSources = prepared.SelectMany(x => x.Sources).OrderBy(x => x.SortOrder).ToList();
        if (allSources.Count == 0)
        {
            CleanupPrepared(prepared);
            return new(false, Error: "receipt scan contains no processable pages.");
        }

        var now = DateTimeOffset.UtcNow;
        var first = allSources[0];
        var row = new ReceiptScanJobRow
        {
            Id = jobId,
            FullWorthSpaceId = fullWorthSpaceId,
            UserId = userId,
            PurchaseId = purchaseId,
            FileName = first.OriginalFileName,
            ContentType = first.MimeType,
            Status = ReceiptScanJobStatuses.Draft,
            Stage = "draft",
            Attempts = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        var committed = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var purchase = new Purchase
            {
                Id = purchaseId,
                FullWorthSpaceId = fullWorthSpaceId,
                Source = "receipt",
                Merchant = string.Empty,
                PurchaseDate = null,
                TotalAmount = 0m,
                Currency = currency,
                Status = "captured",
                ReviewState = "needs_review",
                ReceiptImagePath = first.StoragePath,
                CreatedByUserId = userId,
                Visibility = "space",
                CreatedAt = now,
                UpdatedAt = now
            };
            foreach (var physical in prepared) purchase.Documents.Add(physical.Document);
            db.Purchases.Add(purchase);
            await db.SaveChangesAsync(ct);
            await jobs.CreateAsync(row, ct);
            await jobs.CreateSourcesAsync(allSources, ct);
            await transaction.CommitAsync(ct);
            committed = true;
        }
        catch
        {
            if (!committed) CleanupPrepared(prepared);
            throw;
        }

        var view = await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        return view is null ? new(false, Error: "Receipt draft could not be reloaded.") : new(true, view);
    }

    public async Task<ReceiptScanSourcesOutcome> AddSourcesAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid jobId,
        HttpRequest request,
        CancellationToken ct)
    {
        var job = await DraftForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        if (job.Error is not null) return new(false, Error: job.Error);
        if (!request.HasFormContentType) return new(false, Error: "multipart/form-data is required.");

        var form = await request.ReadFormAsync(ct);
        var files = ReceiptFiles(form);
        if (files.Count == 0) return new(false, Error: "at least one receipt file is required.");
        var sourceIds = ParseSourceIds(form, files.Count);
        if (sourceIds.Error is not null) return new(false, Error: sourceIds.Error);

        var existingSources = await jobs.ListSourcesAsync(jobId, ct);
        var existingIds = existingSources.Select(x => x.Id).ToHashSet();
        var filePairs = files.Select((file, index) => new { file, rootId = sourceIds.Ids[index] }).ToList();

        // Per-file source IDs make an uncertain add-source response idempotent. Existing root IDs are
        // skipped instead of storing the same physical source a second time.
        filePairs = filePairs.Where(pair => !existingIds.Contains(pair.rootId)).ToList();
        if (filePairs.Count == 0) return new(true, existingSources);

        var physicalBytes = await PhysicalBytesAsync(existingSources, ct);
        if (physicalBytes + filePairs.Sum(x => x.file.Length) > storage.MaxReceiptSetBytes)
            return new(false, Error: $"receipt scan set exceeds {storage.MaxReceiptSetBytes} bytes.");

        List<PreparedReceiptFile> prepared;
        try
        {
            prepared = await PrepareFilesAsync(
                filePairs.Select(x => x.file).ToList(),
                filePairs.Select(x => x.rootId).ToList(),
                job.Job!.PurchaseId,
                jobId,
                fullWorthSpaceId,
                userId,
                existingSources.Count == 0 ? 0 : existingSources.Max(x => x.SortOrder) + 1,
                ct);
        }
        catch (ReceiptScanValidationException exception)
        {
            return new(false, Error: exception.Message);
        }

        var newSources = prepared.SelectMany(x => x.Sources).ToList();
        if (existingSources.Count + newSources.Count > storage.MaxReceiptSources)
        {
            CleanupPrepared(prepared);
            return new(false, Error: $"receipt scan may contain at most {storage.MaxReceiptSources} pages/images.");
        }

        var committed = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.PurchaseDocuments.AddRange(prepared.Select(x => x.Document));
            await db.SaveChangesAsync(ct);
            await jobs.CreateSourcesAsync(newSources, ct);
            var combined = existingSources.Concat(newSources).OrderBy(x => x.SortOrder).ToList();
            await UpdateJobSummaryAsync(jobId, combined, ct);
            await transaction.CommitAsync(ct);
            committed = true;
        }
        catch
        {
            if (!committed) CleanupPrepared(prepared);
            throw;
        }

        return new(true, await jobs.ListSourcesAsync(jobId, ct));
    }

    public async Task<ReceiptScanSourcesOutcome> DeleteSourceAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid jobId,
        Guid sourceId,
        CancellationToken ct)
    {
        var job = await DraftForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        if (job.Error is not null) return new(false, Error: job.Error);
        var sources = await jobs.ListSourcesAsync(jobId, ct);
        var source = sources.SingleOrDefault(x => x.Id == sourceId);
        if (source is null) return new(false, Error: "receipt source not found.");

        PurchaseDocument? orphanDocument = null;
        string? orphanPath = null;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await jobs.DeleteSourceAsync(jobId, sourceId, ct);

        if (source.PurchaseDocumentId.HasValue && sources.Count(x => x.PurchaseDocumentId == source.PurchaseDocumentId) == 1)
        {
            orphanDocument = await db.PurchaseDocuments.SingleOrDefaultAsync(x => x.Id == source.PurchaseDocumentId.Value, ct);
            if (orphanDocument is not null)
            {
                orphanPath = orphanDocument.StoragePath;
                db.PurchaseDocuments.Remove(orphanDocument);
                await db.SaveChangesAsync(ct);
            }
        }

        var remaining = sources.Where(x => x.Id != sourceId).OrderBy(x => x.SortOrder).ToList();
        if (remaining.Count > 0)
        {
            await jobs.ReorderSourcesAsync(jobId, remaining.Select(x => x.Id).ToList(), ct);
            remaining = await jobs.ListSourcesAsync(jobId, ct);
        }
        await UpdateJobSummaryAsync(jobId, remaining, ct);
        await transaction.CommitAsync(ct);

        if (orphanDocument is not null && orphanPath is not null) DeleteStoredFileBestEffort(orphanPath);
        return new(true, remaining);
    }

    public async Task<ReceiptScanSourcesOutcome> ReplaceSourceAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid jobId,
        Guid sourceId,
        HttpRequest request,
        CancellationToken ct)
    {
        var job = await DraftForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        if (job.Error is not null) return new(false, Error: job.Error);
        if (!request.HasFormContentType) return new(false, Error: "multipart/form-data is required.");
        var form = await request.ReadFormAsync(ct);
        var files = ReceiptFiles(form);
        if (files.Count != 1) return new(false, Error: "exactly one replacement file is required.");

        var current = await jobs.ListSourcesAsync(jobId, ct);
        var target = current.SingleOrDefault(x => x.Id == sourceId);
        if (target is null) return new(false, Error: "receipt source not found.");

        List<PreparedReceiptFile> prepared;
        try
        {
            prepared = await PrepareFilesAsync(
                files,
                [target.Id],
                job.Job!.PurchaseId,
                jobId,
                fullWorthSpaceId,
                userId,
                2_000_000,
                ct,
                allowExistingDocumentId: target.PurchaseDocumentId,
                enforceSourceLimit: false);
        }
        catch (ReceiptScanValidationException exception)
        {
            return new(false, Error: exception.Message);
        }

        var replacement = prepared.Single();
        if (target.PurchaseDocumentId.HasValue)
        {
            var currentDocument = await db.PurchaseDocuments.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == target.PurchaseDocumentId.Value, ct);
            // A retry after a committed replace is a no-op if it sends the same physical file again.
            if (currentDocument is not null && string.Equals(currentDocument.Sha256, replacement.Document.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                CleanupPrepared(prepared);
                return new(true, current);
            }
        }

        var finalCount = current.Count - 1 + replacement.Sources.Count;
        if (finalCount > storage.MaxReceiptSources)
        {
            CleanupPrepared(prepared);
            return new(false, Error: $"receipt scan may contain at most {storage.MaxReceiptSources} pages/images.");
        }

        var oldDocumentIsOrphan = target.PurchaseDocumentId.HasValue &&
            current.Count(x => x.PurchaseDocumentId == target.PurchaseDocumentId) == 1;
        var currentBytes = await PhysicalBytesAsync(current, ct);
        var oldBytes = oldDocumentIsOrphan && target.PurchaseDocumentId.HasValue
            ? await db.PurchaseDocuments.AsNoTracking().Where(x => x.Id == target.PurchaseDocumentId.Value).Select(x => x.SizeBytes).SingleOrDefaultAsync(ct)
            : 0L;
        if (currentBytes - oldBytes + replacement.Document.SizeBytes > storage.MaxReceiptSetBytes)
        {
            CleanupPrepared(prepared);
            return new(false, Error: $"receipt scan set exceeds {storage.MaxReceiptSetBytes} bytes.");
        }

        var oldPath = oldDocumentIsOrphan && target.PurchaseDocumentId.HasValue
            ? await db.PurchaseDocuments.AsNoTracking().Where(x => x.Id == target.PurchaseDocumentId.Value).Select(x => x.StoragePath).SingleOrDefaultAsync(ct)
            : null;

        var committed = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.PurchaseDocuments.Add(replacement.Document);
            await db.SaveChangesAsync(ct);

            // Preserve the selected source ID so UI focus/provenance links survive replacement. Extra
            // PDF pages get deterministic derived IDs and are inserted at the same position.
            var firstReplacement = replacement.Sources[0];
            firstReplacement.Id = target.Id;
            firstReplacement.SortOrder = target.SortOrder;
            await jobs.UpdateSourceAsync(firstReplacement, ct);
            if (replacement.Sources.Count > 1)
                await jobs.CreateSourcesAsync(replacement.Sources.Skip(1), ct);

            var finalOrder = new List<Guid>();
            foreach (var source in current.OrderBy(x => x.SortOrder))
            {
                if (source.Id != target.Id) finalOrder.Add(source.Id);
                else finalOrder.AddRange(replacement.Sources.Select(x => x.Id));
            }
            await jobs.ReorderSourcesAsync(jobId, finalOrder, ct);

            if (oldDocumentIsOrphan && target.PurchaseDocumentId.HasValue)
            {
                var oldDocument = await db.PurchaseDocuments.SingleOrDefaultAsync(x => x.Id == target.PurchaseDocumentId.Value, ct);
                if (oldDocument is not null)
                {
                    db.PurchaseDocuments.Remove(oldDocument);
                    await db.SaveChangesAsync(ct);
                }
            }

            var finalSources = await jobs.ListSourcesAsync(jobId, ct);
            await UpdateJobSummaryAsync(jobId, finalSources, ct);
            await transaction.CommitAsync(ct);
            committed = true;
        }
        catch
        {
            if (!committed) CleanupPrepared(prepared);
            throw;
        }

        if (oldPath is not null) DeleteStoredFileBestEffort(oldPath);
        return new(true, await jobs.ListSourcesAsync(jobId, ct));
    }

    public async Task<ReceiptScanSourcesOutcome> ReorderAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid jobId,
        ReceiptScanOrderRequest request,
        CancellationToken ct)
    {
        var job = await DraftForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        if (job.Error is not null) return new(false, Error: job.Error);
        var current = await jobs.ListSourcesAsync(jobId, ct);
        var requested = request.SourceIds?.ToList() ?? [];
        if (requested.Count != current.Count || requested.Distinct().Count() != requested.Count ||
            !requested.ToHashSet().SetEquals(current.Select(x => x.Id)))
            return new(false, Error: "source order must contain every current source exactly once.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await jobs.ReorderSourcesAsync(jobId, requested, ct);
        var reordered = await jobs.ListSourcesAsync(jobId, ct);
        await UpdateJobSummaryAsync(jobId, reordered, ct);
        await transaction.CommitAsync(ct);
        return new(true, reordered);
    }

    public async Task<ReceiptScanMutationOutcome> StartAsync(Guid userId, Guid fullWorthSpaceId, Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        if (job is null) return new(false, Error: "receipt scan job not found.");
        if (job.Status != ReceiptScanJobStatuses.Draft) return new(false, Error: "only draft receipt scans can be started.");
        var sources = await jobs.ListSourcesAsync(jobId, ct);
        if (sources.Count == 0) return new(false, Error: "add at least one receipt page/image before starting.");
        if (sources.Count > storage.MaxReceiptSources) return new(false, Error: "receipt scan contains too many sources.");
        if (await PhysicalBytesAsync(sources, ct) > storage.MaxReceiptSetBytes) return new(false, Error: "receipt scan set is too large.");
        foreach (var source in sources)
            if (!File.Exists(SafeAbsolutePath(source.StoragePath))) return new(false, Error: "a stored receipt source is missing.");

        if (!await jobs.StartAsync(userId, fullWorthSpaceId, jobId, ct)) return new(false, Error: "receipt scan could not be queued.");
        return new(true, await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct));
    }

    public async Task<ReceiptScanMutationOutcome> RetryAsync(Guid userId, Guid fullWorthSpaceId, Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        if (job is null) return new(false, Error: "receipt scan job not found.");
        if (job.Status != ReceiptScanJobStatuses.Error) return new(false, Error: "only failed receipt scans can be retried.");
        if (!await jobs.RetryAsync(userId, fullWorthSpaceId, jobId, ct)) return new(false, Error: "receipt scan could not be retried.");
        return new(true, await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct));
    }

    private async Task<(ReceiptScanJobView? Job, string? Error)> DraftForUserAsync(
        Guid userId, Guid fullWorthSpaceId, Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        if (job is null) return (null, "receipt scan job not found.");
        if (job.Status != ReceiptScanJobStatuses.Draft) return (null, "receipt sources can only be edited before analysis starts.");
        return (job, null);
    }

    private async Task<List<PreparedReceiptFile>> PrepareFilesAsync(
        IReadOnlyList<IFormFile> files,
        IReadOnlyList<Guid> rootSourceIds,
        Guid purchaseId,
        Guid jobId,
        Guid fullWorthSpaceId,
        Guid userId,
        int startingSortOrder,
        CancellationToken ct,
        Guid? allowExistingDocumentId = null,
        bool enforceSourceLimit = true)
    {
        if (files.Count != rootSourceIds.Count) throw new ReceiptScanValidationException("source IDs do not match receipt files.");
        if (files.Count > storage.MaxReceiptSources) throw new ReceiptScanValidationException("too many receipt files.");
        if (files.Sum(x => x.Length) > storage.MaxReceiptSetBytes) throw new ReceiptScanValidationException("receipt scan set is too large.");

        var prepared = new List<PreparedReceiptFile>();
        // Track the currently written physical file before validation has produced a PreparedReceiptFile.
        // This closes the orphan window for duplicate detection, unreadable PDFs, source-limit failures and
        // cancellation after FileStream.CreateNew but before prepared.Add(...).
        var unpreparedPaths = new HashSet<string>(StringComparer.Ordinal);
        var batchHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allSourceIds = new HashSet<Guid>();
        var nextOrder = startingSortOrder;
        try
        {
            for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                var file = files[fileIndex];
                if (file.Length <= 0 || file.Length > storage.MaxReceiptBytes)
                    throw new ReceiptScanValidationException("receipt file size is invalid.");

                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (ext is not ".jpg" and not ".jpeg" and not ".png" and not ".webp" and not ".heic" and not ".pdf")
                    throw new ReceiptScanValidationException("unsupported receipt file type.");
                var header = new byte[16];
                int headerRead;
                await using (var probe = file.OpenReadStream())
                    headerRead = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
                if (!ReceiptSignature.Matches(header.AsSpan(0, headerRead), ext))
                    throw new ReceiptScanValidationException("receipt file content does not match its type.");

                var documentId = Guid.NewGuid();
                var now = DateTimeOffset.UtcNow;
                var relative = Path.Combine(now.ToString("yyyy"), now.ToString("MM"), $"{documentId:N}{ext}")
                    .Replace(Path.DirectorySeparatorChar, '/');
                var absolute = SafeAbsolutePath(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                await using (var target = new FileStream(absolute, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await file.CopyToAsync(target, ct);
                unpreparedPaths.Add(absolute);

                string sha256;
                await using (var stored = File.OpenRead(absolute))
                using (var hash = SHA256.Create())
                    sha256 = Convert.ToHexString(await hash.ComputeHashAsync(stored, ct)).ToLowerInvariant();
                if (!batchHashes.Add(sha256))
                    throw new ReceiptScanValidationException("the same receipt file was selected more than once.");

                if (await VisibleDuplicateAsync(userId, fullWorthSpaceId, sha256, allowExistingDocumentId, ct))
                    throw new ReceiptScanValidationException("this receipt file is already stored in this FullWorth Space.");

                var mime = ContentType(ext);
                var pageCount = 1;
                if (ext == ".pdf")
                {
                    try { pageCount = await ReceiptPdfRasterizer.GetPageCountAsync(absolute, storage.MaxReceiptSources, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { throw new ReceiptScanValidationException("receipt PDF could not be read or has too many pages."); }
                }
                // The replace path seeds nextOrder with a high sentinel (sources are reordered before
                // commit) and enforces the source cap itself, so skip this count proxy in that case.
                if (enforceSourceLimit && nextOrder + pageCount > storage.MaxReceiptSources)
                    throw new ReceiptScanValidationException($"receipt scan may contain at most {storage.MaxReceiptSources} pages/images.");

                var rootId = rootSourceIds[fileIndex];
                var sources = new List<ReceiptScanSourceRow>();
                for (var page = 1; page <= pageCount; page++)
                {
                    var sourceId = page == 1 ? rootId : DerivedSourceId(rootId, page);
                    if (!allSourceIds.Add(sourceId)) throw new ReceiptScanValidationException("receipt source IDs must be unique.");
                    sources.Add(new ReceiptScanSourceRow
                    {
                        Id = sourceId,
                        ReceiptScanJobId = jobId,
                        PurchaseDocumentId = documentId,
                        SortOrder = nextOrder++,
                        SourceType = ext == ".pdf" ? "pdf_page" : "image",
                        OriginalFileName = Cap(Path.GetFileName(file.FileName), 500),
                        MimeType = mime,
                        StoragePath = relative,
                        PageNumber = ext == ".pdf" ? page : null,
                        Fingerprint = ext == ".pdf" ? $"{sha256}:page:{page}" : sha256,
                        SizeBytes = file.Length,
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                }

                prepared.Add(new PreparedReceiptFile(
                    new PurchaseDocument
                    {
                        Id = documentId,
                        PurchaseId = purchaseId,
                        DocumentType = "receipt",
                        OriginalFileName = Cap(Path.GetFileName(file.FileName), 500),
                        MediaType = mime,
                        StoragePath = relative,
                        Sha256 = sha256,
                        PageCount = pageCount,
                        SizeBytes = file.Length,
                        Status = "uploaded",
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    sources,
                    absolute));
                unpreparedPaths.Remove(absolute);
            }
            return prepared;
        }
        catch
        {
            CleanupPrepared(prepared);
            CleanupPaths(unpreparedPaths);
            throw;
        }
    }

    private async Task<bool> VisibleDuplicateAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string sha256,
        Guid? excludedDocumentId,
        CancellationToken ct) =>
        await db.PurchaseDocuments.AsNoTracking()
            .Where(document => document.Sha256 == sha256 && document.Purchase.FullWorthSpaceId == fullWorthSpaceId &&
                               (!excludedDocumentId.HasValue || document.Id != excludedDocumentId.Value))
            .Where(document =>
                (document.Purchase.Visibility != "private" || document.Purchase.CreatedByUserId == userId) &&
                (!document.Purchase.PaymentLinks.Any() || document.Purchase.PaymentLinks.Any(link =>
                    db.Transactions.Any(tx => tx.Id == link.TransactionId &&
                        db.Accounts.Any(account => account.Id == tx.AccountId && account.Owners.Any(owner => owner.UserId == userId))))) &&
                (document.Purchase.TransactionId == null || db.Transactions.Any(tx => tx.Id == document.Purchase.TransactionId &&
                    db.Accounts.Any(account => account.Id == tx.AccountId && account.Owners.Any(owner => owner.UserId == userId)))))
            .AnyAsync(ct);

    private async Task<long> PhysicalBytesAsync(IReadOnlyList<ReceiptScanSourceRow> sources, CancellationToken ct)
    {
        var documentIds = sources.Where(x => x.PurchaseDocumentId.HasValue).Select(x => x.PurchaseDocumentId!.Value).Distinct().ToArray();
        if (documentIds.Length == 0) return 0L;
        return await db.PurchaseDocuments.AsNoTracking().Where(x => documentIds.Contains(x.Id)).SumAsync(x => x.SizeBytes, ct);
    }

    private Task UpdateJobSummaryAsync(Guid jobId, IReadOnlyList<ReceiptScanSourceRow> sources, CancellationToken ct)
    {
        var first = sources.OrderBy(x => x.SortOrder).FirstOrDefault();
        var fileName = first?.OriginalFileName ?? string.Empty;
        var contentType = first?.MimeType ?? string.Empty;
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ReceiptScanJobs"
            SET "FileName" = {fileName}, "ContentType" = {contentType}, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {jobId}
            """, ct);
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

    private void DeleteStoredFileBestEffort(string relative)
    {
        try
        {
            var absolute = SafeAbsolutePath(relative);
            if (File.Exists(absolute)) File.Delete(absolute);
        }
        catch { /* DB state is authoritative; orphan-file cleanup can be retried operationally. */ }
    }

    private static List<IFormFile> ReceiptFiles(IFormCollection form)
    {
        var named = form.Files.Where(file => string.Equals(file.Name, "receipt", StringComparison.OrdinalIgnoreCase)).ToList();
        return named.Count > 0 ? named : form.Files.ToList();
    }

    private static (List<Guid> Ids, string? Error) ParseSourceIds(IFormCollection form, int fileCount)
    {
        var raw = form["sourceId"].Concat(form["sourceIds"])
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (raw.Count == 0) return (Enumerable.Range(0, fileCount).Select(_ => Guid.NewGuid()).ToList(), null);
        if (raw.Count != fileCount) return ([], "one sourceId is required for each receipt file.");
        var ids = new List<Guid>(raw.Count);
        foreach (var value in raw)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty) return ([], "sourceId must be a UUID.");
            ids.Add(id);
        }
        if (ids.Distinct().Count() != ids.Count) return ([], "sourceId values must be unique.");
        return (ids, null);
    }

    private static Guid DerivedSourceId(Guid root, int page)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{root:N}:page:{page}"));
        var bytes = hash[..16].ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private static string? NormalizeCurrency(string value)
    {
        var currency = string.IsNullOrWhiteSpace(value) ? "EUR" : value.Trim().ToUpperInvariant();
        return currency.Length == 3 && currency.All(character => character is >= 'A' and <= 'Z') ? currency : null;
    }

    private static string ContentType(string extension) => extension switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];

    private static void CleanupPrepared(IEnumerable<PreparedReceiptFile> prepared)
    {
        foreach (var item in prepared)
        {
            try { if (File.Exists(item.AbsolutePath)) File.Delete(item.AbsolutePath); } catch { }
        }
    }

    private static void CleanupPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private sealed record PreparedReceiptFile(PurchaseDocument Document, List<ReceiptScanSourceRow> Sources, string AbsolutePath);
    private sealed class ReceiptScanValidationException(string message) : Exception(message);
}
