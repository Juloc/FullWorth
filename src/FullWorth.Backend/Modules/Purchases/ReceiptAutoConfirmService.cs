using System.Globalization;
using FullWorth.Backend.Modules.Purchases.Extraction;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases;

/// <summary>
/// Bestaetigt einen Beleg, den die KI ohne Einschraenkung gelesen hat - und nur den.
///
/// Die KI sagt selbst, wann sie unsicher ist: sie schreibt Warnungen ("abgekuerzte Bezeichnung",
/// "Pfandrueckgabe, kein Rabatt") und gibt je Beleg und je Position eine Zuversicht an. Ein Beleg ohne
/// jede Warnung, mit hoher Zuversicht und vollstaendigen Kopfdaten braucht keine Handarbeit; alles
/// andere bleibt auf "zu pruefen". Die Summenprobe (Positionen gegen Belegsumme, Pfand, Rabatt) macht
/// nicht dieser Dienst, sondern <see cref="PurchaseLifecycleService.ConfirmAsync"/> - dieselbe Pruefung
/// wie beim Bestaetigen per Hand, an einer einzigen Stelle.
///
/// Ins Protokoll geht nur der GRUND, warum ein Beleg nicht bestaetigt wurde, nie sein Inhalt.
/// </summary>
public sealed class ReceiptAutoConfirmService(
    PurchaseLifecycleService lifecycle,
    IOptions<ReceiptImportOptions> options,
    ILogger<ReceiptAutoConfirmService> logger)
{
    /// <summary>Darunter gilt die Zuversicht der KI fuer den ganzen Beleg als zu gering.</summary>
    internal const decimal MinReceiptConfidence = 0.8m;

    /// <summary>Darunter gilt die Zuversicht fuer eine einzelne Position oder einen Rabatt als zu gering.</summary>
    internal const decimal MinLineConfidence = 0.7m;

    /// <summary>
    /// Warum dieser Beleg NICHT von selbst bestaetigt wird - oder <c>null</c>, wenn nichts dagegen
    /// spricht. Der Grund ist ein Kennwort, kein Text aus dem Beleg.
    /// </summary>
    internal static string? RefusalReason(CodexReceiptResult result, IReadOnlyList<string> warnings)
    {
        if (warnings.Count > 0) return "ai_warnings";
        if (string.IsNullOrWhiteSpace(result.Merchant.Name)) return "no_merchant";
        if (!DateOnly.TryParse(result.Receipt.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return "no_date";
        if (result.Totals.Total is not > 0m) return "no_total";
        if (result.Items.Count == 0) return "no_items";
        if (result.Confidence < MinReceiptConfidence) return "low_confidence";
        if (result.Items.Any(item => item.Confidence < MinLineConfidence)) return "low_item_confidence";
        if (result.Discounts.Any(discount => discount.Confidence < MinLineConfidence)) return "low_discount_confidence";
        return null;
    }

    /// <summary>
    /// Warum ein lokal gelesener Beleg NICHT von selbst bestaetigt wird. Der Text eines digitalen PDFs ist
    /// exakt - das einzige, was falsch sein kann, ist unser Lesen, und das prueft die Rechnung des Belegs
    /// selbst: nur eine Fassung, deren Summen aufgehen, erreicht <see cref="ReceiptTextParser.TrustedConfidence"/>.
    /// </summary>
    internal static string? RefusalReason(ReceiptExtractionResult result, IReadOnlyList<string> warnings)
    {
        if (warnings.Count > 0) return "warnings";
        if (string.IsNullOrWhiteSpace(result.Merchant)) return "no_merchant";
        if (result.PurchaseDate is null) return "no_date";
        if (result.Total is not > 0m) return "no_total";
        if (result.Items.Count == 0) return "no_items";
        if (result.Confidence < ReceiptTextParser.TrustedConfidence) return "low_confidence";
        return null;
    }

    /// <summary>
    /// Versucht, den Beleg zu bestaetigen. Ein Fehler hier darf den Scan nicht mehr kippen: er ist
    /// dann schon fertig, und der Beleg bleibt einfach auf "zu pruefen".
    /// </summary>
    public Task<bool> TryConfirmAsync(
        ReceiptScanJobRow job,
        CodexReceiptResult result,
        IReadOnlyList<string> warnings,
        CancellationToken ct) => TryConfirmAsync(job, RefusalReason(result, warnings), ct);

    /// <summary>Dasselbe fuer einen Beleg, den der deterministische Leser aus dem Text eines digitalen PDFs gelesen hat.</summary>
    public Task<bool> TryConfirmLocalAsync(
        ReceiptScanJobRow job,
        ReceiptExtractionResult result,
        IReadOnlyList<string> warnings,
        CancellationToken ct) => TryConfirmAsync(job, RefusalReason(result, warnings), ct);

    private async Task<bool> TryConfirmAsync(ReceiptScanJobRow job, string? reason, CancellationToken ct)
    {
        if (!options.Value.AutoConfirm) return false;

        if (reason is not null)
        {
            logger.LogInformation("Receipt scan {JobId} stays in review: {Reason}.", job.Id, reason);
            return false;
        }

        try
        {
            var confirmed = await lifecycle.TryAutoConfirmAsync(job.UserId, job.FullWorthSpaceId, job.PurchaseId, ct);
            logger.LogInformation(confirmed
                ? "Receipt scan {JobId} confirmed automatically."
                : "Receipt scan {JobId} stays in review: the confirmation check did not pass.", job.Id);
            return confirmed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Automatic confirmation of receipt scan {JobId} failed; it stays in review.", job.Id);
            return false;
        }
    }
}
