using System.Security.Cryptography;
using FullWorth.Backend.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.ReceiptImports;

public sealed class ReceiptImportService(
    ReceiptImportStore store,
    ReceiptScanQueueService queue,
    PaperlessReceiptClient paperless,
    FieldCipher cipher,
    IOptions<ReceiptImportOptions> options,
    IOptions<PurchaseStorageOptions> purchaseStorage)
{
    private readonly ReceiptImportOptions settings = options.Value;
    private readonly PurchaseStorageOptions receiptStorage = purchaseStorage.Value;

    public async Task<ReceiptImportBatchView> ImportUploadAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        HttpRequest request,
        CancellationToken ct)
    {
        if (!request.HasFormContentType) throw new ReceiptImportException("multipart/form-data is required.");
        if (request.ContentLength is > 0 && request.ContentLength > settings.MaxUploadBytes)
            throw new ReceiptImportException($"receipt import exceeds {settings.MaxUploadBytes} bytes.");

        var form = await request.ReadFormAsync(new FormOptions
        {
            MultipartBodyLengthLimit = settings.MaxUploadBytes
        }, ct);
        var files = form.Files.GetFiles("receipts");
        if (files.Count == 0) files = form.Files.ToList();
        if (files.Count == 0) throw new ReceiptImportException("at least one receipt file is required.");
        if (files.Count > settings.MaxBatchItems) throw new ReceiptImportException($"a batch may contain at most {settings.MaxBatchItems} receipts.");
        if (files.Sum(file => file.Length) > settings.MaxUploadBytes)
            throw new ReceiptImportException($"receipt import exceeds {settings.MaxUploadBytes} bytes.");

        var currency = NormalizeCurrency(form["currency"].ToString());
        var autoStart = ParseBool(form["autoStart"].ToString(), settings.AutoStart);
        Guid? requestedBatchId = Guid.TryParse(form["clientBatchId"].ToString(), out var parsed) ? parsed : null;
        var batch = await store.CreateBatchAsync(userId, fullWorthSpaceId, ReceiptImportSourceTypes.Upload, "File upload", currency, autoStart, requestedBatchId, ct);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            ReceiptImportItemRow? item = null;
            try
            {
                var fingerprint = await HashAsync(file, ct);
                var externalKey = $"{batch.Id:N}:{fingerprint}";
                var created = await store.CreateItemAsync(
                    batch.Id,
                    fullWorthSpaceId,
                    ReceiptImportSourceTypes.Upload,
                    externalKey,
                    SafeName(file.FileName),
                    null,
                    fingerprint,
                    ct);
                item = created.Item;
                if (!created.Created && created.Item.ReceiptScanJobId.HasValue) continue;

                await QueueFileAsync(userId, fullWorthSpaceId, created.Item, file, currency, autoStart, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (item is not null) await store.MarkFailedAsync(item.Id, SafeError(ex), ct);
            }
        }

        return await store.GetBatchAsync(userId, fullWorthSpaceId, batch.Id, ct)
            ?? throw new ReceiptImportException("receipt import batch could not be reloaded.");
    }

    public async Task<PaperlessConnectionView?> GetPaperlessConnectionAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var stored = await store.GetPaperlessConnectionAsync(fullWorthSpaceId, ct);
        return stored is null ? null : ToView(stored);
    }

    public async Task<(PaperlessConnectionView? Connection, string? ServerVersion, string? Error)> SavePaperlessConnectionAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        PaperlessConnectionWrite request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiToken)) return (null, null, "Paperless API token is required.");
        Uri baseUri;
        try { baseUri = PaperlessReceiptClient.NormalizeBaseUri(request.BaseUrl); }
        catch (UriFormatException ex) { return (null, null, ex.Message); }

        var test = await paperless.TestAsync(baseUri.ToString(), request.ApiToken, ct);
        if (!test.Success) return (null, test.ServerVersion, test.Error);
        var protectedToken = cipher.Protect(request.ApiToken.Trim())
            ?? throw new InvalidOperationException("Paperless token could not be protected.");
        await store.UpsertPaperlessConnectionAsync(fullWorthSpaceId, userId, baseUri.ToString(), protectedToken, request.DefaultQuery, request.IsEnabled, ct);
        var stored = await store.GetPaperlessConnectionAsync(fullWorthSpaceId, ct);
        return (stored is null ? null : ToView(stored), test.ServerVersion, null);
    }

    public async Task DeletePaperlessConnectionAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        await store.DisablePaperlessAutoImportAsync(fullWorthSpaceId, ct);
        await store.DeletePaperlessConnectionAsync(fullWorthSpaceId, ct);
    }

    public async Task<(bool Success, string? ServerVersion, string? Error)> TestPaperlessAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await RequirePaperlessAsync(fullWorthSpaceId, ct);
        return await paperless.TestAsync(connection.BaseUrl, connection.Token, ct);
    }

    public async Task<PaperlessFilterOptionsView> GetPaperlessFilterOptionsAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await RequirePaperlessAsync(fullWorthSpaceId, ct);
        return await paperless.GetFilterOptionsAsync(connection.BaseUrl, connection.Token, ct);
    }

    public async Task<PaperlessPreviewResult> PreviewPaperlessAsync(Guid fullWorthSpaceId, PaperlessPreviewRequest request, CancellationToken ct)
    {
        var connection = await RequirePaperlessAsync(fullWorthSpaceId, ct);
        var effective = ApplyDefaultQuery(request, connection.DefaultQuery);
        var preview = await paperless.PreviewAsync(connection.BaseUrl, connection.Token, effective, ct);
        return await MarkImportedAsync(fullWorthSpaceId, connection.BaseUrl, preview, ct);
    }

    public Task<IReadOnlyList<PaperlessImportPresetView>> ListPaperlessPresetsAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        CancellationToken ct) =>
        store.ListPaperlessPresetsAsync(userId, fullWorthSpaceId, ct);

    public async Task<PaperlessImportPresetView> SavePaperlessPresetAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid? presetId,
        PaperlessImportPresetWrite request,
        CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ReceiptImportException("Preset name is required.");
        if (name.Length > 100) throw new ReceiptImportException("Preset name is too long.");

        var id = presetId is { } value && value != Guid.Empty ? value : Guid.NewGuid();
        var existing = presetId is { } existingId
            ? await store.GetPaperlessPresetAsync(userId, fullWorthSpaceId, existingId, ct)
            : null;
        if (presetId.HasValue && existing is null) throw new ReceiptImportException("Paperless preset was not found.");

        var siblings = await store.ListPaperlessPresetsAsync(userId, fullWorthSpaceId, ct);
        if (siblings.Any(x => x.Id != id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ReceiptImportException("A Paperless preset with this name already exists.");

        var query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
        if (query?.Length > 4000) throw new ReceiptImportException("Paperless query is too long.");
        var editorJson = string.IsNullOrWhiteSpace(request.EditorJson) ? null : request.EditorJson;
        if (editorJson?.Length > 32000) throw new ReceiptImportException("Paperless preset editor data is too large.");

        var currency = NormalizeCurrency(request.Currency);
        var lastSeen = existing?.LastSeenDocumentId;
        var baselineRequired = request.AutoImport &&
            (existing is null || !existing.AutoImport || !string.Equals(existing.Query, query, StringComparison.Ordinal));

        if (baselineRequired)
        {
            var connection = await RequirePaperlessAsync(fullWorthSpaceId, ct);
            var baselineFilter = ApplyDefaultQuery(
                new PaperlessPreviewRequest(Query: query, Limit: settings.MaxBatchItems),
                connection.DefaultQuery);
            var preview = await paperless.PreviewAsync(
                connection.BaseUrl,
                connection.Token,
                baselineFilter,
                ct);
            lastSeen = preview.Documents.Count == 0 ? 0 : preview.Documents.Max(x => x.Id);
        }

        return await store.SavePaperlessPresetAsync(
            id,
            userId,
            fullWorthSpaceId,
            name,
            query,
            editorJson,
            request.AutoImport,
            request.AnalyzeAutomatically,
            currency,
            lastSeen,
            ct);
    }

    public Task DeletePaperlessPresetAsync(Guid userId, Guid fullWorthSpaceId, Guid presetId, CancellationToken ct) =>
        store.DeletePaperlessPresetAsync(userId, fullWorthSpaceId, presetId, ct);

    public async Task RunPaperlessAutoImportPresetAsync(PaperlessAutoImportTarget preset, CancellationToken ct)
    {
        try
        {
            var connection = await RequirePaperlessAsync(preset.FullWorthSpaceId, ct);
            var automaticFilter = ApplyDefaultQuery(
                new PaperlessPreviewRequest(Query: preset.Query, Limit: settings.MaxBatchItems),
                connection.DefaultQuery);
            var preview = await paperless.PreviewAsync(
                connection.BaseUrl,
                connection.Token,
                automaticFilter,
                ct);

            var maxSeen = preview.Documents.Count == 0
                ? preset.LastSeenDocumentId
                : Math.Max(preset.LastSeenDocumentId ?? 0, preview.Documents.Max(x => x.Id));

            var newDocuments = preview.Documents
                .Where(x => x.Id > (preset.LastSeenDocumentId ?? 0))
                .ToList();

            if (newDocuments.Count > 0)
            {
                var imported = await store.GetImportedPaperlessDocumentIdsAsync(
                    preset.FullWorthSpaceId,
                    PaperlessSourcePrefix(connection.BaseUrl),
                    newDocuments.Select(x => x.Id).ToArray(),
                    ct);
                newDocuments = newDocuments.Where(x => !imported.Contains(x.Id)).ToList();
            }

            if (newDocuments.Count == 0)
            {
                await store.UpdatePaperlessPresetCheckAsync(preset.Id, maxSeen, false, null, ct);
                return;
            }

            var batch = await ImportPaperlessAsync(
                preset.UserId,
                preset.FullWorthSpaceId,
                new PaperlessImportRequest(
                    new PaperlessPreviewRequest(Query: preset.Query, Limit: settings.MaxBatchItems),
                    newDocuments.Select(x => x.Id).ToArray(),
                    preset.Currency,
                    preset.AnalyzeAutomatically),
                ct);

            await store.UpdatePaperlessPresetCheckAsync(
                preset.Id,
                batch.Failed == 0 ? maxSeen : null,
                batch.Completed > 0 || batch.Queued > 0 || batch.Processing > 0 || batch.NeedsReview > 0,
                batch.Failed > 0 ? $"{batch.Failed} receipt(s) failed during automatic import." : null,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await store.UpdatePaperlessPresetCheckAsync(preset.Id, null, false, SafeError(ex), ct);
        }
    }

    private async Task<PaperlessPreviewResult> MarkImportedAsync(
        Guid fullWorthSpaceId,
        string baseUrl,
        PaperlessPreviewResult preview,
        CancellationToken ct)
    {
        if (preview.Documents.Count == 0) return preview;
        var imported = await store.GetImportedPaperlessDocumentIdsAsync(
            fullWorthSpaceId,
            PaperlessSourcePrefix(baseUrl),
            preview.Documents.Select(x => x.Id).ToArray(),
            ct);
        if (imported.Count == 0) return preview;
        return preview with
        {
            Documents = preview.Documents.Select(x => x with { Imported = imported.Contains(x.Id) }).ToList()
        };
    }

    public async Task<ReceiptImportBatchView> ImportPaperlessAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        PaperlessImportRequest request,
        CancellationToken ct)
    {
        var connection = await RequirePaperlessAsync(fullWorthSpaceId, ct);
        var filter = ApplyDefaultQuery(request.Filter, connection.DefaultQuery);
        IReadOnlyList<PaperlessDocumentSummary> documents;
        if (request.DocumentIds is { Count: > 0 })
        {
            var wanted = request.DocumentIds.Distinct().Take(settings.MaxBatchItems).ToHashSet();
            var preview = await paperless.PreviewAsync(connection.BaseUrl, connection.Token, filter with { Limit = settings.MaxBatchItems }, ct);
            documents = preview.Documents.Where(x => wanted.Contains(x.Id)).ToList();
        }
        else
        {
            var preview = await paperless.PreviewAsync(connection.BaseUrl, connection.Token, filter with { Limit = settings.MaxBatchItems }, ct);
            documents = preview.Documents;
        }
        if (documents.Count == 0) throw new ReceiptImportException("Paperless filter returned no importable documents.");

        var autoStart = request.AutoStart ?? settings.AutoStart;
        var currency = NormalizeCurrency(request.Currency);
        var batch = await store.CreateBatchAsync(userId, fullWorthSpaceId, ReceiptImportSourceTypes.Paperless, "Paperless-ngx", currency, autoStart, null, ct);
        var sourcePrefix = PaperlessSourcePrefix(connection.BaseUrl);

        foreach (var document in documents)
        {
            ct.ThrowIfCancellationRequested();
            var externalKey = $"{sourcePrefix}:{document.Id}";
            var prior = await store.FindSourceAsync(fullWorthSpaceId, ReceiptImportSourceTypes.Paperless, externalKey, ct);
            var created = await store.CreateItemAsync(
                batch.Id,
                fullWorthSpaceId,
                ReceiptImportSourceTypes.Paperless,
                externalKey,
                string.IsNullOrWhiteSpace(document.Title) ? $"Paperless {document.Id}" : document.Title,
                $"paperless:{document.Id}",
                null,
                ct);

            if (!created.Created && created.Item.ReceiptScanJobId.HasValue) continue;
            if (prior is not null && prior.BatchId != batch.Id && prior.ReceiptScanJobId.HasValue && prior.JobStatus != ReceiptScanJobStatuses.Error)
            {
                await store.MarkSkippedDuplicateAsync(created.Item.Id, "Paperless document was imported previously.", ct);
                continue;
            }

            try
            {
                await using var download = await paperless.DownloadAsync(connection.BaseUrl, connection.Token, document.Id, ct);
                var fingerprint = await HashAsync(download.Content, ct);
                await store.UpdateFingerprintAsync(created.Item.Id, fingerprint, ct);
                download.Content.Position = 0;
                var file = CreateFormFile(download.Content, download.FileName, download.ContentType);
                await QueueFileAsync(userId, fullWorthSpaceId, created.Item, file, currency, autoStart, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await store.MarkFailedAsync(created.Item.Id, SafeError(ex), ct);
            }
        }

        await store.TouchPaperlessSyncAsync(fullWorthSpaceId, ct);
        return await store.GetBatchAsync(userId, fullWorthSpaceId, batch.Id, ct)
            ?? throw new ReceiptImportException("Paperless import batch could not be reloaded.");
    }

    public async Task<FolderPreviewResult> PreviewFolderAsync(CancellationToken ct)
    {
        var root = GetFolderRoot();
        if (root is null) return new(false, null, 0, 0, [], false);
        var files = await DiscoverFolderAsync(root, settings.MaxBatchItems + 1, ct);
        var truncated = files.Count > settings.MaxBatchItems;
        var visible = files.Take(settings.MaxBatchItems).ToList();
        return new(true, root, visible.Count, visible.Sum(x => x.SizeBytes), visible.Select(x => x.RelativePath).ToList(), truncated);
    }

    public async Task<ReceiptImportBatchView> ImportFolderAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string? currency,
        bool? autoStart,
        CancellationToken ct)
    {
        var root = GetFolderRoot() ?? throw new ReceiptImportException("receipt import folder is not configured.");
        // Existing archive files do not consume the new-import batch limit. This matters for watched/read-only
        // NAS folders where thousands of already imported receipts remain in place forever.
        var files = await DiscoverFolderForImportAsync(root, fullWorthSpaceId, ct);
        if (files.Count == 0) throw new ReceiptImportException("receipt import folder contains no stable supported files.");

        var start = autoStart ?? settings.AutoStart;
        var normalizedCurrency = NormalizeCurrency(currency);
        var batch = await store.CreateBatchAsync(userId, fullWorthSpaceId, ReceiptImportSourceTypes.Folder, "Import folder", normalizedCurrency, start, null, ct);
        foreach (var source in files)
        {
            ct.ThrowIfCancellationRequested();
            var prior = await store.FindSourceAsync(fullWorthSpaceId, ReceiptImportSourceTypes.Folder, source.Fingerprint, ct);
            var created = await store.CreateItemAsync(batch.Id, fullWorthSpaceId, ReceiptImportSourceTypes.Folder,
                source.Fingerprint, source.FileName, source.RelativePath, source.Fingerprint, ct);
            if (!created.Created && created.Item.ReceiptScanJobId.HasValue) continue;
            if (prior is not null && prior.BatchId != batch.Id && prior.ReceiptScanJobId.HasValue && prior.JobStatus != ReceiptScanJobStatuses.Error)
            {
                await store.MarkSkippedDuplicateAsync(created.Item.Id, "Folder file was imported previously.", ct);
                continue;
            }

            try
            {
                await using var stream = new FileStream(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var file = CreateFormFile(stream, source.FileName, ContentTypeFor(source.FileName));
                await QueueFileAsync(userId, fullWorthSpaceId, created.Item, file, normalizedCurrency, start, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await store.MarkFailedAsync(created.Item.Id, SafeError(ex), ct);
            }
        }

        return await store.GetBatchAsync(userId, fullWorthSpaceId, batch.Id, ct)
            ?? throw new ReceiptImportException("folder import batch could not be reloaded.");
    }

    public async Task<ReceiptImportBatchView?> StartPendingAsync(Guid userId, Guid fullWorthSpaceId, Guid batchId, CancellationToken ct)
    {
        var view = await store.GetBatchAsync(userId, fullWorthSpaceId, batchId, ct);
        if (view is null) return null;
        foreach (var item in view.Items.Where(x => x.ReceiptScanJobId.HasValue && x.JobStatus == ReceiptScanJobStatuses.Draft))
        {
            var started = await queue.StartAsync(userId, fullWorthSpaceId, item.ReceiptScanJobId!.Value, ct);
            if (started.Success && item.PurchaseId.HasValue)
                await store.MarkQueuedAsync(item.Id, item.ReceiptScanJobId.Value, item.PurchaseId.Value, ct);
            else if (!started.Success)
                await store.MarkFailedAsync(item.Id, started.Error ?? "receipt analysis could not be started.", ct);
        }
        return await store.GetBatchAsync(userId, fullWorthSpaceId, batchId, ct);
    }

    public async Task<ReceiptImportBatchView?> RetryFailedAsync(Guid userId, Guid fullWorthSpaceId, Guid batchId, CancellationToken ct)
    {
        var view = await store.GetBatchAsync(userId, fullWorthSpaceId, batchId, ct);
        if (view is null) return null;

        foreach (var item in view.Items.Where(x => x.Status == ReceiptImportItemStatuses.Failed))
        {
            ct.ThrowIfCancellationRequested();
            if (item.ReceiptScanJobId.HasValue && item.JobStatus == ReceiptScanJobStatuses.Error)
            {
                var retried = await queue.RetryAsync(userId, fullWorthSpaceId, item.ReceiptScanJobId.Value, ct);
                if (retried.Success && item.PurchaseId.HasValue)
                    await store.MarkQueuedAsync(item.Id, item.ReceiptScanJobId.Value, item.PurchaseId.Value, ct);
                else if (!retried.Success)
                    await store.MarkFailedAsync(item.Id, retried.Error ?? "receipt analysis could not be retried.", ct);
                continue;
            }

            if (item.ReceiptScanJobId.HasValue) continue;
            try
            {
                await RetryImportStageAsync(userId, fullWorthSpaceId, view.Batch, item, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await store.MarkFailedAsync(item.Id, SafeError(ex), ct);
            }
        }

        return await store.GetBatchAsync(userId, fullWorthSpaceId, batchId, ct);
    }

    private async Task RetryImportStageAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        ReceiptImportBatchRow batch,
        ReceiptImportItemRow item,
        CancellationToken ct)
    {
        switch (item.SourceType)
        {
            case ReceiptImportSourceTypes.Paperless:
                await RetryPaperlessItemAsync(userId, fullWorthSpaceId, batch, item, ct);
                return;
            case ReceiptImportSourceTypes.Folder:
                await RetryFolderItemAsync(userId, fullWorthSpaceId, batch, item, ct);
                return;
            case ReceiptImportSourceTypes.Upload:
                throw new ReceiptImportException("Browser upload bytes are no longer available; select this file again in a new import.");
            default:
                throw new ReceiptImportException("This import source cannot be retried automatically.");
        }
    }

    private async Task RetryPaperlessItemAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        ReceiptImportBatchRow batch,
        ReceiptImportItemRow item,
        CancellationToken ct)
    {
        var documentId = ParsePaperlessDocumentId(item)
            ?? throw new ReceiptImportException("Paperless document identity is missing from the failed import item.");
        var connection = await RequirePaperlessAsync(fullWorthSpaceId, ct);
        await using var download = await paperless.DownloadAsync(connection.BaseUrl, connection.Token, documentId, ct);
        var fingerprint = await HashAsync(download.Content, ct);
        await store.UpdateFingerprintAsync(item.Id, fingerprint, ct);
        download.Content.Position = 0;
        var file = CreateFormFile(download.Content, download.FileName, download.ContentType);
        await QueueFileAsync(userId, fullWorthSpaceId, item, file, batch.Currency, batch.AutoStart, ct);
        await store.TouchPaperlessSyncAsync(fullWorthSpaceId, ct);
    }

    private async Task RetryFolderItemAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        ReceiptImportBatchRow batch,
        ReceiptImportItemRow item,
        CancellationToken ct)
    {
        var root = GetFolderRoot() ?? throw new ReceiptImportException("receipt import folder is not configured.");
        var relative = item.SourceReference?.Trim();
        if (string.IsNullOrWhiteSpace(relative))
            throw new ReceiptImportException("Folder source reference is missing from the failed import item.");

        var source = await FindFolderSourceAsync(root, relative, ct)
            ?? throw new ReceiptImportException("The original folder file is no longer available or stable.");
        if (!string.IsNullOrWhiteSpace(item.ContentFingerprint) &&
            !string.Equals(item.ContentFingerprint, source.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new ReceiptImportException("The folder file changed after the failed import; import it as a new receipt instead.");

        await using var stream = new FileStream(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var file = CreateFormFile(stream, source.FileName, ContentTypeFor(source.FileName));
        await QueueFileAsync(userId, fullWorthSpaceId, item, file, batch.Currency, batch.AutoStart, ct);
    }

    private async Task QueueFileAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        ReceiptImportItemRow item,
        IFormFile source,
        string currency,
        bool autoStart,
        CancellationToken ct)
    {
        await using var stream = source.OpenReadStream();
        var outcome = await queue.EnqueueProviderAsync(
            userId,
            fullWorthSpaceId,
            new ReceiptQueueCreateRequest(
                item.Id,
                currency,
                new[]
                {
                    new ReceiptQueueFile(
                        stream,
                        SafeName(source.FileName),
                        source.ContentType,
                        source.Length,
                        item.Id)
                }),
            ct);

        if (!outcome.Success || outcome.Job is null)
        {
            if (IsDuplicate(outcome.Error))
            {
                await store.MarkSkippedDuplicateAsync(item.Id, outcome.Error, ct);
                return;
            }
            throw new ReceiptImportException(outcome.Error ?? "receipt could not be queued.");
        }

        await store.MarkQueuedAsync(item.Id, outcome.Job.Id, outcome.Job.PurchaseId, ct);
        if (autoStart)
        {
            var start = await queue.StartAsync(userId, fullWorthSpaceId, outcome.Job.Id, ct);
            if (!start.Success) throw new ReceiptImportException(start.Error ?? "receipt analysis could not be started.");
        }
    }

    private async Task<PaperlessRuntimeConnection> RequirePaperlessAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var stored = await store.GetPaperlessConnectionAsync(fullWorthSpaceId, ct)
            ?? throw new ReceiptImportException("Paperless is not configured for this FullWorth Space.");
        if (!stored.IsEnabled) throw new ReceiptImportException("Paperless connection is disabled.");
        var token = cipher.Unprotect(stored.ApiTokenProtected);
        if (string.IsNullOrWhiteSpace(token)) throw new ReceiptImportException("Paperless token could not be loaded.");
        return new(stored.BaseUrl, token, stored.DefaultQuery);
    }

    private async Task<List<FolderReceiptFile>> DiscoverFolderForImportAsync(string root, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var stableBefore = now.AddSeconds(-Math.Max(1, settings.FolderStableAgeSeconds));
        var newFiles = new List<FolderReceiptFile>();
        var duplicateOnlyFallback = new List<FolderReceiptFile>();

        foreach (var path in EnumerateSafeFiles(root, settings.FolderRecursive))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsSupported(path)) continue;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > receiptStorage.MaxReceiptBytes) continue;
            var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (lastWrite > stableBefore) continue;

            var fingerprint = await HashAsync(path, ct);
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var candidate = new FolderReceiptFile(relative, path, info.Name, info.Length, lastWrite, fingerprint);
            var prior = await store.FindSourceAsync(fullWorthSpaceId, ReceiptImportSourceTypes.Folder, fingerprint, ct);
            var alreadyImported = prior is not null && prior.ReceiptScanJobId.HasValue && prior.JobStatus != ReceiptScanJobStatuses.Error;
            if (alreadyImported)
            {
                if (duplicateOnlyFallback.Count < settings.MaxBatchItems) duplicateOnlyFallback.Add(candidate);
                continue;
            }

            newFiles.Add(candidate);
            if (newFiles.Count >= settings.MaxBatchItems) break;
        }

        // If there is anything new, spend the whole batch on useful work. When there is nothing new, retain
        // duplicate rows so a manual re-scan still gives the user an explicit "already imported" summary.
        return newFiles.Count > 0 ? newFiles : duplicateOnlyFallback;
    }

    private async Task<List<FolderReceiptFile>> DiscoverFolderAsync(string root, int limit, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var stableBefore = now.AddSeconds(-Math.Max(1, settings.FolderStableAgeSeconds));
        var result = new List<FolderReceiptFile>();
        foreach (var path in EnumerateSafeFiles(root, settings.FolderRecursive))
        {
            ct.ThrowIfCancellationRequested();
            if (result.Count >= limit) break;
            if (!IsSupported(path)) continue;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > receiptStorage.MaxReceiptBytes) continue;
            var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (lastWrite > stableBefore) continue;
            var fingerprint = await HashAsync(path, ct);
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            result.Add(new(relative, path, info.Name, info.Length, lastWrite, fingerprint));
        }
        return result;
    }

    private async Task<FolderReceiptFile?> FindFolderSourceAsync(string root, string relativePath, CancellationToken ct)
    {
        var stableBefore = DateTimeOffset.UtcNow.AddSeconds(-Math.Max(1, settings.FolderStableAgeSeconds));
        foreach (var path in EnumerateSafeFiles(root, settings.FolderRecursive))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (!string.Equals(relative, relativePath.Replace('\\', '/'), StringComparison.Ordinal)) continue;
            if (!IsSupported(path)) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > receiptStorage.MaxReceiptBytes) return null;
            var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            if (lastWrite > stableBefore) return null;
            var fingerprint = await HashAsync(path, ct);
            return new(relative, path, info.Name, info.Length, lastWrite, fingerprint);
        }
        return null;
    }

    private static int? ParsePaperlessDocumentId(ReceiptImportItemRow item)
    {
        var reference = item.SourceReference?.Trim();
        if (!string.IsNullOrWhiteSpace(reference) && reference.StartsWith("paperless:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(reference["paperless:".Length..], out var id) && id > 0)
            return id;
        var separator = item.ExternalKey.LastIndexOf(':');
        return separator >= 0 && int.TryParse(item.ExternalKey[(separator + 1)..], out id) && id > 0 ? id : null;
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root, bool recursive)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
            if (!recursive) continue;
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(directory); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var child in dirs)
            {
                if (Path.GetFileName(child).Equals(".fullworth", StringComparison.OrdinalIgnoreCase)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                pending.Push(child);
            }
        }
    }

    private string? GetFolderRoot()
    {
        if (!settings.FolderEnabled || string.IsNullOrWhiteSpace(settings.InboxPath)) return null;
        var root = Path.GetFullPath(settings.InboxPath.Trim());
        if (!Directory.Exists(root)) return null;
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string PaperlessSourcePrefix(string baseUrl) =>
        PaperlessReceiptClient.NormalizeBaseUri(baseUrl).GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    private static PaperlessPreviewRequest ApplyDefaultQuery(PaperlessPreviewRequest request, string? defaultQuery) =>
        string.IsNullOrWhiteSpace(request.Query) && !string.IsNullOrWhiteSpace(defaultQuery)
            ? request with { Query = defaultQuery }
            : request;

    private static PaperlessConnectionView ToView(PaperlessStoredConnection stored) =>
        new(stored.FullWorthSpaceId, stored.BaseUrl, true, stored.DefaultQuery, stored.IsEnabled, stored.LastSyncAt, stored.UpdatedAt);

    private static IFormFile CreateFormFile(Stream stream, string fileName, string contentType) =>
        new FormFile(stream, 0, stream.Length, "receipt", SafeName(fileName))
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };

    private static async Task<string> HashAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return await HashAsync(stream, ct);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await HashAsync(stream, ct);
    }

    private static async Task<string> HashAsync(Stream stream, CancellationToken ct)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var hash = SHA256.Create();
        var value = Convert.ToHexString(await hash.ComputeHashAsync(stream, ct)).ToLowerInvariant();
        if (stream.CanSeek) stream.Position = 0;
        return value;
    }

    private string NormalizeCurrency(string? currency)
    {
        var value = string.IsNullOrWhiteSpace(currency) ? settings.DefaultCurrency : currency.Trim();
        return value.Length == 3 && value.All(char.IsLetter) ? value.ToUpperInvariant() : "EUR";
    }

    private static bool ParseBool(string? value, bool fallback) => bool.TryParse(value, out var parsed) ? parsed : fallback;
    private static bool IsDuplicate(string? error) => !string.IsNullOrWhiteSpace(error) &&
        (error.Contains("already stored", StringComparison.OrdinalIgnoreCase) || error.Contains("same receipt file", StringComparison.OrdinalIgnoreCase));
    private static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp" or ".heic" or ".pdf";
    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };
    private static string SafeName(string name) => Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "receipt" : name).Trim();
    private static string SafeError(Exception ex) => ex is ReceiptImportException or InvalidOperationException
        ? (ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000])
        : "Receipt import failed.";

    private sealed record PaperlessRuntimeConnection(string BaseUrl, string Token, string? DefaultQuery);
}

public sealed class ReceiptImportException(string message) : Exception(message);
