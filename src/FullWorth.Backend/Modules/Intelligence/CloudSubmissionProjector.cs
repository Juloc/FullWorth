using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record CloudFeedbackProjection(
    string SubjectType,
    string SubjectKey,
    string SemanticMappingKey,
    string Source = "user",
    decimal Confidence = 1m);

/// <summary>
/// Converts explicitly whitelisted, public/stable identifiers into the network outbox contract.
/// This class deliberately rejects local UUIDs, arbitrary text and predictable hashes. A feedback
/// event can remain useful locally even when it cannot safely be projected to FullWorth Cloud.
/// </summary>
public static class CloudSubmissionProjector
{
    public static bool IsSafe(CloudFeedbackProjection? projection)
    {
        if (projection is null) return false;
        if (!string.Equals(projection.SubjectType, "product", StringComparison.Ordinal)) return false;
        if (!IsValidGtinSubjectKey(projection.SubjectKey)) return false;
        if (!IsSemanticKey(projection.SemanticMappingKey)) return false;
        if (projection.Confidence is < 0m or > 1m) return false;
        return string.Equals(projection.Source, "user", StringComparison.Ordinal);
    }

    public static async Task<CloudSubmissionOutbox?> TryCreateOutboxAsync(
        IntelligenceDbContext db,
        IntelligenceFeedbackEvent feedback,
        CloudFeedbackProjection? projection,
        CancellationToken ct)
    {
        if (!feedback.CloudEligible || !IsSafe(projection)) return null;

        var state = await db.CloudConnectionStates.AsNoTracking()
            .Where(x => x.Mode == CloudIntelligenceModes.Enabled)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (state is null) return null;

        var hasConsent = await db.CloudIntelligenceConsents.AsNoTracking().AnyAsync(x =>
            x.InstanceId == state.InstanceId &&
            x.PolicyVersion == CloudIntelligencePolicy.CurrentVersion &&
            x.RevokedAt == null, ct);
        if (!hasConsent) return null;

        var payload = JsonSerializer.Serialize(new
        {
            subject = new
            {
                type = projection!.SubjectType,
                key = projection.SubjectKey
            },
            mapping = new
            {
                categoryKey = projection.SemanticMappingKey
            },
            source = projection.Source,
            confidence = projection.Confidence,
            observedMonth = feedback.CreatedAt.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)
        });

        return new CloudSubmissionOutbox
        {
            InstanceId = state.InstanceId,
            FeedbackEventId = feedback.Id,
            IdempotencyKey = $"feedback:{feedback.Id:N}:schema:{CloudIntelligencePolicy.SubmissionSchemaVersion}",
            SchemaVersion = CloudIntelligencePolicy.SubmissionSchemaVersion,
            EventType = feedback.EventType,
            PayloadJson = payload,
            Status = CloudSubmissionStatuses.Queued,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static bool TryCreateGtinSubjectKey(string? barcode, out string? subjectKey)
    {
        subjectKey = null;
        if (string.IsNullOrWhiteSpace(barcode)) return false;
        var raw = barcode.Trim();
        if (raw.Any(ch => !char.IsDigit(ch) && ch is not (' ' or '-'))) return false;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length is not (8 or 12 or 13 or 14)) return false;
        if (!HasValidGtinCheckDigit(digits)) return false;
        subjectKey = $"gtin:{digits}";
        return true;
    }

    private static bool IsValidGtinSubjectKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("gtin:", StringComparison.Ordinal)) return false;
        return TryCreateGtinSubjectKey(value[5..], out var normalized) &&
               string.Equals(normalized, value, StringComparison.Ordinal);
    }

    private static bool IsSemanticKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100) return false;
        foreach (var ch in value)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')) return false;
        }
        return true;
    }

    private static bool HasValidGtinCheckDigit(string digits)
    {
        var sum = 0;
        var weightThree = true;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            var digit = digits[i] - '0';
            sum += digit * (weightThree ? 3 : 1);
            weightThree = !weightThree;
        }
        var expected = (10 - (sum % 10)) % 10;
        return digits[^1] - '0' == expected;
    }
}
