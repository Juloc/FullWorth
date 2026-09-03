using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.Merchants;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Best-effort append-only feedback capture for explicit user decisions. Primary finance mutations
/// must never fail because intelligence persistence is unavailable. Payloads are intentionally small,
/// semantic and free of raw bank payloads, notes, receipt images or provider credentials.
/// </summary>
public sealed class IntelligenceFeedbackRecorder(
    IntelligenceDbContext db,
    ILogger<IntelligenceFeedbackRecorder> logger)
{
    public Task<bool> RecordProductCategoryAsync(
        Guid fullWorthSpaceId,
        Guid userId,
        Guid productId,
        string normalizedAlias,
        Guid? oldCategoryId,
        Guid newCategoryId,
        CancellationToken ct,
        string? publicProductKey = null,
        string? semanticCategoryKey = null)
    {
        _ = publicProductKey;
        _ = semanticCategoryKey;
        return TryRecordAsync(new IntelligenceFeedbackEvent
        {
            FullWorthSpaceId = fullWorthSpaceId,
            UserId = userId,
            EventType = "product_category_corrected",
            SubjectType = "product",
            SubjectId = productId.ToString("D"),
            SubjectFingerprint = Fingerprint("product-alias", normalizedAlias),
            OldValueJson = JsonSerializer.Serialize(new { categoryId = oldCategoryId }),
            NewValueJson = JsonSerializer.Serialize(new { categoryId = newCategoryId, productId }),
            Source = "user",
            CloudEligible = false,
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
    }

    public Task<bool> RecordContractDecisionAsync(
        Guid fullWorthSpaceId,
        Guid userId,
        string counterparty,
        string currency,
        bool accepted,
        Guid? contractId,
        string? billingCycle,
        int? interval,
        CancellationToken ct) => TryRecordAsync(new IntelligenceFeedbackEvent
        {
            FullWorthSpaceId = fullWorthSpaceId,
            UserId = userId,
            EventType = accepted ? "contract_candidate_accepted" : "contract_candidate_rejected",
            SubjectType = "contract_candidate",
            SubjectId = contractId?.ToString("D") ?? Fingerprint("contract-candidate-id", $"{Normalize(counterparty)}|{NormalizeCurrency(currency)}"),
            SubjectFingerprint = Fingerprint("contract-candidate", $"{Normalize(counterparty)}|{NormalizeCurrency(currency)}"),
            OldValueJson = "{}",
            NewValueJson = JsonSerializer.Serialize(new
            {
                accepted,
                contractId,
                currency = NormalizeCurrency(currency),
                billingCycle = NormalizeOptional(billingCycle),
                interval
            }),
            Source = "user",
            // A predictable counterparty hash is useful for local dedupe, but is not a safe public
            // cloud identifier. This becomes cloud-eligible only after a canonical provider key exists.
            CloudEligible = false,
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);

    /// <summary>
    /// Records a manual category correction only. It intentionally does not create future merchant
    /// mappings or rules. Future learning is an explicit user action handled by category-intelligence
    /// learning endpoints, so "this transaction" can never silently affect later transactions.
    /// </summary>
    public Task<bool> RecordCategoryDecisionAsync(
        Guid fullWorthSpaceId,
        Guid userId,
        Guid transactionId,
        string? normalizedCounterparty,
        string direction,
        Guid? oldCategoryId,
        Guid? newCategoryId,
        string action,
        CancellationToken ct)
    {
        var normalizedMerchant = MerchantNormalization.Normalize(normalizedCounterparty);
        var normalizedDirection = NormalizeDirection(direction);
        return TryRecordAsync(new IntelligenceFeedbackEvent
        {
            FullWorthSpaceId = fullWorthSpaceId,
            UserId = userId,
            EventType = action,
            SubjectType = "transaction_category",
            SubjectId = transactionId.ToString("D"),
            SubjectFingerprint = Fingerprint("merchant-direction", $"{normalizedMerchant}|{normalizedDirection}"),
            OldValueJson = JsonSerializer.Serialize(new { categoryId = oldCategoryId }),
            NewValueJson = JsonSerializer.Serialize(new { categoryId = newCategoryId }),
            Source = "user",
            // Normalized counterparty text and an unhashed/predictably hashed merchant string are not
            // sufficient cloud identities. Keep the feedback local until merchant canonicalization can
            // supply a stable public FullWorth merchant/provider key.
            CloudEligible = false,
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
    }

    private async Task<bool> TryRecordAsync(
        IntelligenceFeedbackEvent feedback,
        CancellationToken ct)
    {
        try
        {
            db.IntelligenceFeedbackEvents.Add(feedback);
            // Feedback capture is best-effort. The primary FinanceDb mutation that triggered this
            // recorder remains intentionally independent and is never rolled back by it.
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (db.Entry(feedback).State != EntityState.Detached)
                db.Entry(feedback).State = EntityState.Detached;
            logger.LogWarning(exception,
                "Intelligence feedback capture failed for {EventType}/{SubjectType}; primary finance mutation remains successful.",
                feedback.EventType, feedback.SubjectType);
            return false;
        }
    }

    private static string Fingerprint(string domain, string? value)
    {
        var material = Encoding.UTF8.GetBytes($"fullworth:{domain}:v1:{Normalize(value)}");
        return "sha256:" + Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeCurrency(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string? NormalizeDirection(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "income" => "income",
        "expense" => "expense",
        _ => null
    };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Normalize(value);
}
