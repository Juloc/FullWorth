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
        CancellationToken ct,
        string? cloudMerchantAlias = null,
        string? categoryKey = null,
        string? categoryName = null,
        bool categoryIsCustom = false,
        string? categoryLocale = null)
    {
        var normalizedMerchant = MerchantNormalization.Normalize(normalizedCounterparty);
        var normalizedDirection = NormalizeDirection(direction);
        var cloudAlias = MerchantNormalization.Normalize(cloudMerchantAlias);
        var cloudEligible = cloudAlias is not null &&
                            normalizedDirection is not null &&
                            !string.IsNullOrWhiteSpace(categoryKey) &&
                            !string.IsNullOrWhiteSpace(categoryName);

        var feedback = new IntelligenceFeedbackEvent
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
            CloudEligible = cloudEligible,
            CreatedAt = DateTimeOffset.UtcNow
        };

        CloudOutboxProjection? projection = null;
        if (cloudEligible)
        {
            projection = new CloudOutboxProjection(
                "merchant_mapping",
                JsonSerializer.Serialize(new
                {
                    alias = cloudAlias,
                    mapping = new
                    {
                        categoryKey = categoryKey!.Trim(),
                        categoryAlias = categoryName!.Trim(),
                        categoryLocale = NormalizeLocale(categoryLocale),
                        categoryIsCustom
                    },
                    direction = normalizedDirection,
                    action = "corrected",
                    confidence = 1m,
                    observedMonth = DateTimeOffset.UtcNow.ToString("yyyy-MM")
                }));
        }

        return TryRecordAsync(feedback, ct, projection);
    }

    private async Task<bool> TryRecordAsync(
        IntelligenceFeedbackEvent feedback,
        CancellationToken ct,
        CloudOutboxProjection? cloudProjection = null)
    {
        try
        {
            db.IntelligenceFeedbackEvents.Add(feedback);

            if (feedback.CloudEligible && cloudProjection is not null)
            {
                var state = await db.CloudConnectionStates.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.ScopeKey == CloudConnectionState.InstanceScopeKey &&
                        x.Mode == CloudIntelligenceModes.Enabled, ct);
                if (state is not null)
                {
                    var hasCurrentConsent = await db.CloudIntelligenceConsents.AsNoTracking().AnyAsync(x =>
                        x.InstanceId == state.InstanceId &&
                        x.PolicyVersion == CloudIntelligencePolicy.CurrentVersion &&
                        x.RevokedAt == null, ct);
                    if (hasCurrentConsent)
                    {
                        db.CloudSubmissionOutbox.Add(new CloudSubmissionOutbox
                        {
                            InstanceId = state.InstanceId,
                            FeedbackEventId = feedback.Id,
                            IdempotencyKey = $"feedback:{feedback.Id:N}:schema:{CloudIntelligencePolicy.SubmissionSchemaVersion}",
                            SchemaVersion = CloudIntelligencePolicy.SubmissionSchemaVersion,
                            EventType = cloudProjection.EventType,
                            PayloadJson = cloudProjection.PayloadJson,
                            Status = CloudSubmissionStatuses.Queued,
                            CreatedAt = DateTimeOffset.UtcNow
                        });
                    }
                }
            }

            // Feedback + outbox commit together. The primary FinanceDb mutation that triggered this
            // recorder remains intentionally independent and is never rolled back by cloud persistence.
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

    private static string NormalizeLocale(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "und";
        var primary = value.Split(',', StringSplitOptions.RemoveEmptyEntries)[0]
            .Split(';', StringSplitOptions.RemoveEmptyEntries)[0]
            .Trim()
            .Replace('_', '-')
            .ToLowerInvariant();
        if (primary.Length is < 2 or > 20) return "und";
        return primary.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') ? primary : "und";
    }

    private sealed record CloudOutboxProjection(string EventType, string PayloadJson);
}
