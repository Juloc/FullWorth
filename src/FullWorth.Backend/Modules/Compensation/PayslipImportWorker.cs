namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Holt und liest die gefundenen Lohnabrechnungen in kleinen Portionen. Solange es Arbeit gibt, laeuft
/// er in kurzem Takt, sonst wartet er still. Jeder Fund behaelt seinen Status, also ist die Portion
/// zugleich der Wiederaufnahmepunkt nach einem Neustart.
/// </summary>
public sealed class PayslipImportWorker(IServiceScopeFactory scopeFactory, ILogger<PayslipImportWorker> logger) : BackgroundService
{
    // Klein, weil ein Fund Sekunden bis Minuten dauert (OCR, dann Codex) und sich nichts stauen soll.
    private const int BatchSize = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                using var scope = scopeFactory.CreateScope();
                processed = await scope.ServiceProvider.GetRequiredService<PayslipImportService>().ProcessPendingAsync(BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reading pending payslip imports failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(processed >= BatchSize ? 2 : 10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
