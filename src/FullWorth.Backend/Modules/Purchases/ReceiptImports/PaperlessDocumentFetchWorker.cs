using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.ReceiptImports;

/// <summary>
/// Holt die wartenden Paperless-Belege in kleinen Portionen (#127).
///
/// Vorher lief die Schleife im HTTP-Request des Importdialogs: 115 Dokumente hiessen 115 Downloads,
/// bevor der Benutzer eine Antwort sah, und eine abgebrochene Verbindung liess den halben Import als
/// "ausstehend" liegen, ohne dass irgendetwas ihn wieder aufgenommen haette.
///
/// Jetzt legt der Request nur die Zeilen an. Dieser Dienst nimmt sie portionsweise, mit einer Pause
/// dazwischen - und weil jede geholte Zeile ihren Status behaelt, ist die Portion zugleich der
/// Wiederaufnahmepunkt: nach einem Neustart wird kein einziges Dokument ein zweites Mal angefragt.
///
/// Solange es Arbeit gibt, laeuft er in kurzem Takt; ist nichts da, wartet er still.
/// </summary>
public sealed class PaperlessDocumentFetchWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ReceiptImportOptions> options,
    ILogger<PaperlessDocumentFetchWorker> logger) : BackgroundService
{
    private readonly ReceiptImportOptions settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(settings.PaperlessFetchIntervalSeconds, 2, 300));
        var batchSize = Math.Clamp(settings.PaperlessFetchBatchSize, 1, 50);

        while (!stoppingToken.IsCancellationRequested)
        {
            var fetched = 0;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<ReceiptImportService>();
                fetched = await service.FetchPendingPaperlessAsync(batchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fetching pending Paperless documents failed.");
            }

            // Eine volle Portion heisst: es liegt noch mehr. Dann wird kuerzer gewartet, aber nie gar
            // nicht - die Pause zwischen den Portionen ist der Punkt.
            var wait = fetched >= batchSize ? TimeSpan.FromSeconds(2) : interval;
            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
