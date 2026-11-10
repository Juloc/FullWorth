using System.Security.Cryptography;
using FullWorth.Backend.Modules.Paperless;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.ReceiptImports;

public sealed class ReceiptImportService(
    ReceiptImportStore store,
    ReceiptScanQueueService queue,
    PaperlessClient paperless,
    PaperlessService paperlessService,
    PaperlessStore paperlessStore,
    IOptions<ReceiptImportOptions> options,
    IOptions<PurchaseStorageOptions> purchaseStorage) : IPaperlessPresetImporter
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

                // The batch has the last word, not the request: uploading into a paused batch adds the
                // receipt and leaves it waiting. Otherwise a pause lasted exactly until the next file.
                await QueueFileAsync(userId, fullWorthSpaceId, created.Item, file, currency, autoStart && !batch.IsPaused, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (item is not null) await store.MarkFailedAsync(item.Id, SafeError(ex), ct);
            }
        }

        return await store.GetBatchAsync(userId, fullWorthSpaceId, batch.Id, ct)
            ?? throw new ReceiptImportException("receipt import batch could not be reloaded.");
    }

    public async Task<PaperlessPreviewResult> PreviewPaperlessAsync(Guid fullWorthSpaceId, PaperlessPreviewRequest request, CancellationToken ct)
    {
        var connection = await paperlessService.RequireAsync(fullWorthSpaceId, ct);
        var effective = PaperlessService.ApplyDefaultQuery(request, connection.DefaultQuery);
        var preview = await paperless.PreviewAsync(connection.BaseUrl, connection.Token, effective, ct);
        return await MarkImportedAsync(fullWorthSpaceId, connection.BaseUrl, preview, ct);
    }

    public IReadOnlyCollection<string> Kinds => [PaperlessPresetKinds.Receipt];

    public async Task<PaperlessImportOutcome> ImportAsync(PaperlessImportPresetView preset, PaperlessConnection connection, CancellationToken ct)
    {
        try
        {
            var automaticFilter = PaperlessService.ApplyDefaultQuery(
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
                    PaperlessService.SourcePrefix(connection.BaseUrl),
                    newDocuments.Select(x => x.Id).ToArray(),
                    ct);
                imported.UnionWith(await OwnDocumentIdsAsync(preset.FullWorthSpaceId, newDocuments, ct));
                newDocuments = newDocuments.Where(x => !imported.Contains(x.Id)).ToList();
            }

            if (newDocuments.Count == 0) return new(maxSeen, false, null);

            var batch = await ImportPaperlessAsync(
                preset.UserId,
                preset.FullWorthSpaceId,
                new PaperlessImportRequest(
                    new PaperlessPreviewRequest(Query: preset.Query, Limit: settings.MaxBatchItems),
                    newDocuments.Select(x => x.Id).ToArray(),
                    preset.Currency,
                    preset.AnalyzeAutomatically),
                ct);

            return new(
                batch.Failed == 0 ? maxSeen : null,
                batch.Completed > 0 || batch.Queued > 0 || batch.Processing > 0 || batch.NeedsReview > 0,
                batch.Failed > 0 ? $"{batch.Failed} receipt(s) failed during automatic import." : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(null, false, SafeError(ex));
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
            PaperlessService.SourcePrefix(baseUrl),
            preview.Documents.Select(x => x.Id).ToArray(),
            ct);
        imported.UnionWith(await OwnDocumentIdsAsync(fullWorthSpaceId, preview.Documents, ct));
        if (imported.Count == 0) return preview;
        return preview with
        {
            Documents = preview.Documents.Select(x => x with { Imported = imported.Contains(x.Id) }).ToList()
        };
    }

    /// <summary>
    /// Die Dokumente der Liste, die FullWorth selbst nach Paperless abgelegt hat. Sie liegen schon als
    /// Kauf vor; wuerden sie wieder importiert, stuende derselbe Einkauf zweimal in den Auswertungen.
    /// </summary>
    private async Task<HashSet<int>> OwnDocumentIdsAsync(
        Guid fullWorthSpaceId,
        IReadOnlyList<PaperlessDocumentSummary> documents,
        CancellationToken ct)
    {
        var checksums = documents.Where(x => !string.IsNullOrWhiteSpace(x.Checksum)).Select(x => x.Checksum!).ToArray();
        var own = await store.GetOwnPaperlessChecksumsAsync(fullWorthSpaceId, checksums, ct);
        return documents.Where(x => x.Checksum is not null && own.Contains(x.Checksum)).Select(x => x.Id).ToHashSet();
    }

    public async Task<ReceiptImportBatchView> ImportPaperlessAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        PaperlessImportRequest request,
        CancellationToken ct)
    {
        var connection = await paperlessService.RequireAsync(fullWorthSpaceId, ct);
        var filter = PaperlessService.ApplyDefaultQuery(request.Filter, connection.DefaultQuery);
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
        var sourcePrefix = PaperlessService.SourcePrefix(connection.BaseUrl);

        var ownDocuments = await OwnDocumentIdsAsync(fullWorthSpaceId, documents, ct);
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
                ct,
                // Was Paperless ohnehin mitgeliefert hat, bleibt am Beleg stehen (#128) - sonst muesste
                // FullWorth spaeter fuer jedes Dokument noch einmal nachfragen.
                new ReceiptSourceMetadata(
                    document.Created,
                    document.MimeType,
                    document.Correspondent?.ToString(),
                    [.. document.Tags.Select(tag => tag.ToString())],
                    document.Content,
                    document.Modified));

            if (!created.Created && created.Item.ReceiptScanJobId.HasValue) continue;
            if (ownDocuments.Contains(document.Id))
            {
                await store.MarkSkippedDuplicateAsync(created.Item.Id, "Dieser Beleg stammt aus FullWorth und liegt schon als Kauf vor.", ct);
                continue;
            }
            if (prior is not null && prior.BatchId != batch.Id && prior.ReceiptScanJobId.HasValue && prior.JobStatus != ReceiptScanJobStatuses.Error)
            {
                await store.MarkSkippedDuplicateAsync(created.Item.Id, "Paperless document was imported previously.", ct);
                continue;
            }

            // Hier wurde frueher heruntergeladen - im HTTP-Request, ein Dokument nach dem anderen. Bei
            // 115 Dokumenten hiess das 115 Downloads, bevor der Benutzer eine Antwort sah, und eine
            // abgebrochene Verbindung liess den halben Import als "ausstehend" zurueck (#127).
            //
            // Die Zeile ist jetzt der Auftrag. Sie steht mit Status "pending" in der Datenbank, und der
            // Hintergrunddienst holt sie in kleinen Portionen. Das ist zugleich der Wiederaufnahmepunkt:
            // was schon geholt wurde, wird nie ein zweites Mal angefragt.
        }

        await paperlessStore.TouchPaperlessSyncAsync(fullWorthSpaceId, ct);
        return await store.GetBatchAsync(userId, fullWorthSpaceId, batch.Id, ct)
            ?? throw new ReceiptImportException("Paperless import batch could not be reloaded.");
    }

    /// <summary>
    /// Holt bis zu <paramref name="limit"/> wartende Paperless-Belege und stellt sie in die
    /// Verarbeitung (#127).
    ///
    /// Nacheinander, nicht gleichzeitig: die Begrenzung im Client deckelt zwar die Anfragen, aber der
    /// Sinn der Portion ist, dass zwischen zwei Portionen eine Pause liegt. Gibt die Zahl der geholten
    /// Belege zurueck, damit der Dienst weiss, ob es sich lohnt, gleich weiterzumachen.
    /// </summary>
    public async Task<int> FetchPendingPaperlessAsync(int limit, CancellationToken ct)
    {
        var pending = await store.ListPendingPaperlessItemsAsync(limit, ct);
        if (pending.Count == 0) return 0;

        var fetched = 0;
        // Eine Verbindung je Bereich, nicht je Beleg - die Einstellungen aendern sich waehrend eines
        // Imports nicht, und jedes Mal neu zu laden waere eine Datenbankabfrage pro Dokument.
        var connections = new Dictionary<Guid, PaperlessConnection?>();

        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!connections.TryGetValue(item.FullWorthSpaceId, out var connection))
            {
                // Eine abgeschaltete oder geloeschte Verbindung ist kein Fehler des Belegs - aber ohne sie
                // laesst er sich nicht holen, und das gehoert an die Zeile geschrieben.
                try { connection = await paperlessService.RequireAsync(item.FullWorthSpaceId, ct); }
                catch (PaperlessException) { connection = null; }
                connections[item.FullWorthSpaceId] = connection;
            }

            var documentId = ParsePaperlessDocumentId(item.SourceReference);
            if (connection is null || documentId is null)
            {
                await store.MarkFailedAsync(item.ItemId, "Paperless connection is no longer configured.", ct);
                continue;
            }

            var row = await store.GetItemAsync(item.BatchId, item.ItemId, ct);
            if (row is null) continue;

            try
            {
                await using var download = await paperless.DownloadAsync(
                    connection.BaseUrl, connection.Token, documentId.Value, ct);
                var fingerprint = await HashAsync(download.Content, ct);
                await store.UpdateFingerprintAsync(item.ItemId, fingerprint, ct);
                download.Content.Position = 0;
                var file = CreateFormFile(download.Content, download.FileName, download.ContentType);
                await QueueFileAsync(item.UserId, item.FullWorthSpaceId, row, file, item.Currency, item.AutoStart, ct);
                fetched++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await store.MarkFailedAsync(item.ItemId, SafeError(ex), ct);
            }
        }
        return fetched;
    }

    /// <summary>Die Dokumentnummer aus "paperless:123". Null, wenn dort etwas anderes steht.</summary>
    private static int? ParsePaperlessDocumentId(string? sourceReference)
    {
        var text = sourceReference?.Trim();
        if (string.IsNullOrEmpty(text) || !text.StartsWith("paperless:", StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(text.AsSpan("paperless:".Length), out var id) ? id : null;
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

    /// <summary>
    /// Stops the batch taking new work. What is already in <c>processing</c> finishes — see
    /// <see cref="ReceiptImportStore.WithdrawQueuedJobsAsync"/> for why interrupting it would be worse.
    /// </summary>
    public async Task<ReceiptImportBatchView?> PauseAsync(Guid userId, Guid fullWorthSpaceId, Guid batchId, CancellationToken ct)
    {
        var view = await store.GetBatchAsync(userId, fullWorthSpaceId, batchId, ct);
        if (view is null) return null;
        // The flag first, then the withdrawal: in the other order a job finishing in between could
        // trigger the next one and the batch would keep moving after the user was told it stopped.
        await store.SetBatchPausedAsync(batchId, DateTimeOffset.UtcNow, ct);
        await store.WithdrawQueuedJobsAsync(batchId, ct);
        return await store.GetBatchAsync(userId, fullWorthSpaceId, batchId, ct);
    }

    /// <summary>Lifts the pause and starts everything that is waiting — the same path as starting.</summary>
    public async Task<ReceiptImportBatchView?> ResumeAsync(Guid userId, Guid fullWorthSpaceId, Guid batchId, CancellationToken ct)
    {
        var view = await store.GetBatchAsync(userId, fullWorthSpaceId, batchId, ct);
        if (view is null) return null;
        await store.SetBatchPausedAsync(batchId, null, ct);
        return await StartPendingAsync(userId, fullWorthSpaceId, batchId, ct);
    }

    /// <summary>
    /// Starts one receipt, without touching the rest of the batch.
    ///
    /// The queue could always do this — <see cref="StartPendingAsync"/> has been calling it per job in a
    /// loop all along — but the only way in from outside was "start everything pending". That is the
    /// wrong granularity for the case it is needed in: one receipt out of ninety is the interesting
    /// one, or one failed differently from the others and deserves a single retry.
    ///
    /// Deliberately allowed while the batch is paused. Pause stops the machine from taking work on its
    /// own; it does not stop the owner from asking for exactly one thing.
    /// </summary>
    public async Task<ReceiptImportBatchView?> StartItemAsync(
        Guid userId, Guid fullWorthSpaceId, Guid batchId, Guid itemId, CancellationToken ct)
    {
        var view = await store.GetBatchAsync(userId, fullWorthSpaceId, batchId, ct);
        if (view is null) return null;
        var item = view.Items.FirstOrDefault(x => x.Id == itemId);
        if (item is null) return null;
        if (!item.ReceiptScanJobId.HasValue)
            throw new ReceiptImportException("This receipt has not been uploaded yet, so there is nothing to analyse.");

        var outcome = item.JobStatus == ReceiptScanJobStatuses.Error
            ? await queue.RetryAsync(userId, fullWorthSpaceId, item.ReceiptScanJobId.Value, ct)
            : await queue.StartAsync(userId, fullWorthSpaceId, item.ReceiptScanJobId.Value, ct);

        if (outcome.Success && item.PurchaseId.HasValue)
            await store.MarkQueuedAsync(item.Id, item.ReceiptScanJobId.Value, item.PurchaseId.Value, ct);
        else if (!outcome.Success)
            throw new ReceiptImportException(outcome.Error ?? "receipt analysis could not be started.");

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

    /// <summary>
    /// Nimmt einen abgeschlossenen Belegimport-Stapel zurueck (#141): Kaeufe, die dieser Stapel angelegt
    /// hat und an denen der Nutzer seither nicht weitergearbeitet hat, verschwinden wieder - samt ihrer
    /// gespeicherten Belegdatei. Ein Kauf, der inzwischen bezahlt, zurueckgegeben oder bestaetigt wurde,
    /// bleibt stehen; siehe PurchaseImportProvenance fuer was genau als "weitergearbeitet" zaehlt.
    /// </summary>
    public async Task<ReceiptImportRollbackResult?> RollbackBatchAsync(Guid userId, Guid fullWorthSpaceId, Guid batchId, CancellationToken ct)
    {
        var view = await store.GetBatchAsync(userId, fullWorthSpaceId, batchId, ct);
        if (view is null) return null;
        if (view.Batch.IsRolledBack)
            throw new ReceiptImportException("This import has already been rolled back.");

        var linked = await store.ImportLinkCountAsync(batchId, ct);
        if (linked == 0)
            throw new ReceiptImportException("This import predates rollback tracking, or created no purchases, so automatic rollback is not available.");

        var (removed, filePaths) = await store.RollbackBatchAsync(batchId, ct);
        PurchaseStorageFiles.Delete(receiptStorage, filePaths);
        return new ReceiptImportRollbackResult(removed, linked - removed);
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
        var connection = await paperlessService.RequireAsync(fullWorthSpaceId, ct);
        await using var download = await paperless.DownloadAsync(connection.BaseUrl, connection.Token, documentId, ct);
        var fingerprint = await HashAsync(download.Content, ct);
        await store.UpdateFingerprintAsync(item.Id, fingerprint, ct);
        download.Content.Position = 0;
        var file = CreateFormFile(download.Content, download.FileName, download.ContentType);
        await QueueFileAsync(userId, fullWorthSpaceId, item, file, batch.Currency, batch.AutoStart && !batch.IsPaused, ct);
        await paperlessStore.TouchPaperlessSyncAsync(fullWorthSpaceId, ct);
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
        await QueueFileAsync(userId, fullWorthSpaceId, item, file, batch.Currency, batch.AutoStart && !batch.IsPaused, ct);
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

    /// <summary>Ob sich der Beleg eines Kaufs nach Paperless ablegen laesst - und wenn nicht, warum nicht, in einem Satz fuer den Benutzer.</summary>
    public async Task<PaperlessUploadStatus> GetPaperlessUploadStatusAsync(Guid fullWorthSpaceId, Guid purchaseId, CancellationToken ct)
    {
        var connection = await paperlessStore.GetPaperlessConnectionAsync(fullWorthSpaceId, ct);
        if (connection is not { IsEnabled: true }) return new(false, false, "Paperless ist nicht verbunden.", null);
        var existing = await store.GetPaperlessUploadAsync(fullWorthSpaceId, purchaseId, ct);
        if (existing is not null) return new(true, false, "Dieser Beleg liegt schon in Paperless.", existing.UploadedAt);
        var files = await store.ListPurchaseSourceFilesAsync(fullWorthSpaceId, purchaseId, ct);
        return PaperlessUploadBlocker(files) is { } blocker ? new(true, false, blocker, null) : new(true, true, null, null);
    }

    /// <summary>
    /// Legt den Beleg eines Kaufs - die Fotos als ein PDF, ein hochgeladenes PDF unveraendert - mit Titel,
    /// Datum und, wenn es ihn dort gibt, dem Korrespondenten des Haendlers in Paperless ab.
    ///
    /// Die Ablage wird vor dem Senden eingetragen und bei einem Fehler wieder entfernt: so gibt es nie
    /// einen Moment, in dem Paperless die Datei hat und FullWorth sie nicht als eigene kennt - und ein
    /// zweiter Klick legt sie nicht noch einmal ab.
    /// </summary>
    public async Task<PaperlessUploadStatus> UploadToPaperlessAsync(Guid userId, Guid fullWorthSpaceId, PurchaseView purchase, CancellationToken ct)
    {
        var connection = await paperlessService.RequireAsync(fullWorthSpaceId, ct);
        if (await store.GetPaperlessUploadAsync(fullWorthSpaceId, purchase.Id, ct) is not null)
            throw new ReceiptImportException("Dieser Beleg liegt schon in Paperless.");
        var files = await store.ListPurchaseSourceFilesAsync(fullWorthSpaceId, purchase.Id, ct);
        if (PaperlessUploadBlocker(files) is { } blocker) throw new ReceiptImportException(blocker);

        var contents = new List<byte[]>();
        foreach (var file in files) contents.Add(await File.ReadAllBytesAsync(PurchaseStorageFiles.Resolve(receiptStorage, file.StoragePath), ct));
        if (contents.Sum(x => (long)x.Length) > MaxPaperlessUploadBytes)
            throw new ReceiptImportException("Der Beleg ist zu gross, um ihn nach Paperless abzulegen.");
        var isPdf = IsPdf(files[0]);
        var document = isPdf ? contents[0] : ImagePdf.Build(contents);
        var checksum = Convert.ToHexString(MD5.HashData(document)).ToLowerInvariant();

        var date = purchase.PurchaseDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var merchant = string.IsNullOrWhiteSpace(purchase.Merchant) ? null : purchase.Merchant.Trim();
        var title = merchant is null
            ? $"Beleg {date:yyyy-MM-dd}"
            : $"{merchant} {date:yyyy-MM-dd} {purchase.TotalAmount.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("de-DE"))} {purchase.Currency}";

        if (!await store.AddPaperlessUploadAsync(fullWorthSpaceId, purchase.Id, userId, checksum, null, ct))
            throw new ReceiptImportException("Diese Datei liegt schon in Paperless.");
        try
        {
            var correspondent = merchant is null ? null : await paperless.FindCorrespondentIdAsync(connection.BaseUrl, connection.Token, merchant, ct);
            var outcome = await paperless.UploadAsync(connection.BaseUrl, connection.Token,
                new PaperlessUploadRequest(document, $"beleg-{date:yyyyMMdd}.pdf", "application/pdf", title, date, correspondent), ct);
            if (!outcome.Success) throw new ReceiptImportException(outcome.Error ?? "Paperless hat die Ablage abgelehnt.");
        }
        catch (Exception ex)
        {
            // Ohne Eintrag gilt der Beleg wieder als nicht abgelegt. CancellationToken.None, weil genau ein
            // abgebrochener Aufruf diesen Eintrag sonst stehen liesse.
            await store.RemovePaperlessUploadAsync(fullWorthSpaceId, purchase.Id, CancellationToken.None);
            if (ex is ReceiptImportException or OperationCanceledException) throw;
            throw new ReceiptImportException(ex is HttpRequestException or TaskCanceledException or UriFormatException
                ? "Paperless ist nicht erreichbar."
                : SafeError(ex));
        }
        return await GetPaperlessUploadStatusAsync(fullWorthSpaceId, purchase.Id, ct);
    }

    /// <summary>Das, was einer Ablage im Weg steht, als Satz; null, wenn sich die Dateien zu einem Dokument zusammenfassen lassen.</summary>
    private static string? PaperlessUploadBlocker(IReadOnlyList<PurchaseSourceFile> files)
    {
        if (files.Count == 0) return "Zu diesem Kauf gibt es keine Belegdatei.";
        if (files.Count == 1 && IsPdf(files[0])) return null;
        return files.All(x => string.Equals(x.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            ? null
            : "Nur Fotos im JPEG-Format oder ein einzelnes PDF lassen sich als ein Dokument ablegen.";
    }

    private static bool IsPdf(PurchaseSourceFile file) => string.Equals(file.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);

    private const long MaxPaperlessUploadBytes = 100L * 1024 * 1024;

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

}

public sealed class ReceiptImportException(string message) : Exception(message);
