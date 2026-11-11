using FullWorth.Backend.Modules.Paperless;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Lohnabrechnungen und Arbeitsvertraege aus Paperless. Zwei Schritte, wie bei den Belegen (#127): ein Preset-Lauf legt nur
/// die Funde an (eine Anfrage an Paperless, ein paar Zeilen), und der <see cref="DocumentImportWorker"/>
/// holt und liest sie portionsweise - das Auslesen dauert (OCR, dann Codex) und gehoert in keinen
/// HTTP-Request. Nichts wird hier bestaetigt: ein ausgelesener Fund wartet, bis der Benutzer die Werte
/// geprueft und gespeichert hat.
/// </summary>
public sealed class DocumentImportService(
    DocumentImportStore imports,
    PayslipStore payslips,
    IEnumerable<IDocumentReader> readers,
    PaperlessClient paperless,
    PaperlessService connections,
    IOptions<PaperlessOptions> options,
    ILogger<DocumentImportService> logger) : IPaperlessPresetImporter
{
    public IReadOnlyCollection<string> Kinds => [DocumentKinds.Payslip, DocumentKinds.Contract];

    public async Task<PaperlessImportOutcome> ImportAsync(PaperlessImportPresetView preset, PaperlessConnection connection, CancellationToken ct)
    {
        try
        {
            var source = PaperlessService.SourcePrefix(connection.BaseUrl);
            var listing = await paperless.PreviewAsync(
                connection.BaseUrl,
                connection.Token,
                PaperlessService.ApplyDefaultQuery(new PaperlessPreviewRequest(Query: preset.Query, Limit: options.Value.MaxBatchItems), connection.DefaultQuery),
                ct);
            var (knownIds, knownChecksums) = await imports.GetKnownAsync(preset.UserId, preset.FullWorthSpaceId, source, listing.Documents, ct);

            var found = 0;
            foreach (var document in listing.Documents)
            {
                if (knownIds.Contains(document.Id) || (document.Checksum is not null && knownChecksums.Contains(document.Checksum))) continue;
                var url = new Uri(PaperlessClient.NormalizeBaseUri(connection.BaseUrl), $"documents/{document.Id}/details").ToString();
                if (await imports.AddPendingAsync(preset.UserId, preset.FullWorthSpaceId, preset.Kind, source, document, url, ct)) found++;
            }
            return new(null, found > 0, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            return new(null, false, ex.Message);
        }
    }

    /// <summary>Holt und liest bis zu <paramref name="limit"/> wartende Funde. Gibt zurueck, wie viele es waren.</summary>
    public async Task<int> ProcessPendingAsync(int limit, CancellationToken ct)
    {
        var pending = await imports.ListPendingAsync(limit, ct);
        // Eine Verbindung je Space, nicht je Fund - sie aendert sich waehrend eines Durchgangs nicht.
        var spaces = new Dictionary<Guid, PaperlessConnection?>();
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!spaces.TryGetValue(item.FullWorthSpaceId, out var connection))
            {
                try { connection = await connections.RequireAsync(item.FullWorthSpaceId, ct); }
                catch (PaperlessException) { connection = null; }
                spaces[item.FullWorthSpaceId] = connection;
            }

            try
            {
                // Dokumentnummern gelten nur in ihrer Instanz: nach einem Wechsel der Verbindung wuerde
                // dieselbe Nummer ein fremdes Dokument holen.
                if (connection is null || PaperlessService.SourcePrefix(connection.BaseUrl) != item.PaperlessSource)
                    throw new InvalidOperationException("Die Paperless-Verbindung dieses Fundes besteht nicht mehr.");

                await using var download = await paperless.DownloadAsync(connection.BaseUrl, connection.Token, item.PaperlessDocumentId, ct);
                var paperlessText = await paperless.GetContentAsync(connection.BaseUrl, connection.Token, item.PaperlessDocumentId, ct);
                var result = await readers.Single(reader => reader.Kind == item.Kind).ReadAsync(item.UserId, download.Content, download.FileName, paperlessText, ct);
                await imports.StoreReadAsync(item.Id, download.Content.ToArray(), download.FileName, download.ContentType, result, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reading {Kind} import {ImportId} failed.", item.Kind, item.Id);
                await imports.RecordFailureAsync(
                    item.Id,
                    ex is ArgumentException or InvalidOperationException ? ex.Message : "Die Abrechnung konnte nicht gelesen werden.",
                    ct);
            }
        }
        return pending.Count;
    }

    /// <summary>Loescht eine Abrechnung und ihr abgelegtes Original, falls sie eins hat.</summary>
    public async Task<bool?> DeletePayslipAsync(Guid userId, Guid fullWorthSpaceId, Guid payslipId, CancellationToken ct)
    {
        var deleted = await payslips.DeleteAsync(userId, fullWorthSpaceId, payslipId, ct);
        if (deleted == true) await imports.ReleaseConfirmedAsync(userId, payslipId, ct);
        return deleted;
    }
}
