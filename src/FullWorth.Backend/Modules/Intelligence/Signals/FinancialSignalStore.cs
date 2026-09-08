using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence.Signals;

public sealed record FinancialSignalView(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid UserId,
    string Type,
    string SubjectType,
    string SubjectId,
    string SemanticKey,
    string Source,
    string Severity,
    decimal Confidence,
    decimal? ImpactAmount,
    string? ImpactCurrency,
    string TitleKey,
    JsonElement Payload,
    JsonElement Evidence,
    decimal RankScore,
    DateTimeOffset DetectedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? ResolvedAt,
    int Version,
    string State,
    DateTimeOffset? SnoozedUntil,
    string? Feedback);

public sealed class FinancialSignalStore(IntelligenceDbContext db)
{
    public async Task<FinancialSignal> UpsertAsync(DetectedFinancialSignal detected, CancellationToken ct)
    {
        Validate(detected);

        var row = await db.FinancialSignals.SingleOrDefaultAsync(x =>
            x.UserId == detected.UserId &&
            x.FullWorthSpaceId == detected.FullWorthSpaceId &&
            x.SemanticKey == detected.SemanticKey, ct);

        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new FinancialSignal
            {
                FullWorthSpaceId = detected.FullWorthSpaceId,
                UserId = detected.UserId,
                Type = detected.Type.Trim(),
                SubjectType = detected.SubjectType.Trim(),
                SubjectId = detected.SubjectId.Trim(),
                SemanticKey = detected.SemanticKey.Trim(),
                Source = detected.Source.Trim(),
                Severity = detected.Severity.Trim().ToLowerInvariant(),
                Confidence = detected.Confidence,
                ImpactAmount = detected.ImpactAmount,
                ImpactCurrency = NormalizeCurrency(detected.ImpactCurrency),
                TitleKey = detected.TitleKey.Trim(),
                PayloadJson = detected.PayloadJson,
                EvidenceJson = detected.EvidenceJson,
                RankScore = detected.RankScore,
                DetectedAt = detected.DetectedAt,
                UpdatedAt = now,
                ValidUntil = detected.ValidUntil,
                Version = 1
            };
            db.FinancialSignals.Add(row);
            await db.SaveChangesAsync(ct);
            return row;
        }

        var meaningfulChange =
            row.ResolvedAt.HasValue ||
            !string.Equals(row.Type, detected.Type.Trim(), StringComparison.Ordinal) ||
            !string.Equals(row.SubjectType, detected.SubjectType.Trim(), StringComparison.Ordinal) ||
            !string.Equals(row.SubjectId, detected.SubjectId.Trim(), StringComparison.Ordinal) ||
            !string.Equals(row.Source, detected.Source.Trim(), StringComparison.Ordinal) ||
            !string.Equals(row.Severity, detected.Severity.Trim(), StringComparison.OrdinalIgnoreCase) ||
            row.Confidence != detected.Confidence ||
            row.ImpactAmount != detected.ImpactAmount ||
            !string.Equals(row.ImpactCurrency, NormalizeCurrency(detected.ImpactCurrency), StringComparison.Ordinal) ||
            !string.Equals(row.TitleKey, detected.TitleKey.Trim(), StringComparison.Ordinal) ||
            !JsonEquivalent(row.PayloadJson, detected.PayloadJson) ||
            !JsonEquivalent(row.EvidenceJson, detected.EvidenceJson);

        if (meaningfulChange)
        {
            row.Type = detected.Type.Trim();
            row.SubjectType = detected.SubjectType.Trim();
            row.SubjectId = detected.SubjectId.Trim();
            row.Source = detected.Source.Trim();
            row.Severity = detected.Severity.Trim().ToLowerInvariant();
            row.Confidence = detected.Confidence;
            row.ImpactAmount = detected.ImpactAmount;
            row.ImpactCurrency = NormalizeCurrency(detected.ImpactCurrency);
            row.TitleKey = detected.TitleKey.Trim();
            row.PayloadJson = detected.PayloadJson;
            row.EvidenceJson = detected.EvidenceJson;
            row.DetectedAt = detected.DetectedAt;
            row.ResolvedAt = null;
            row.Version += 1;

            var state = await db.FinancialSignalStates.SingleOrDefaultAsync(x =>
                x.SignalId == row.Id && x.UserId == row.UserId, ct);
            if (state is not null && state.State is FinancialSignalStates.Dismissed or FinancialSignalStates.Snoozed)
            {
                state.State = FinancialSignalStates.Unread;
                state.SnoozedUntil = null;
                state.UpdatedAt = now;
            }
        }

        row.RankScore = detected.RankScore;
        row.ValidUntil = detected.ValidUntil;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<int> ResolveMissingBySourceAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string source,
        IReadOnlySet<string> activeSemanticKeys,
        DateTimeOffset resolvedAt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Signal source is required.", nameof(source));
        var normalizedSource = source.Trim();
        var rows = await db.FinancialSignals.Where(x =>
            x.UserId == userId &&
            x.FullWorthSpaceId == fullWorthSpaceId &&
            x.Source == normalizedSource &&
            x.ResolvedAt == null).ToListAsync(ct);

        var changed = 0;
        foreach (var row in rows)
        {
            if (activeSemanticKeys.Contains(row.SemanticKey)) continue;
            row.ResolvedAt = resolvedAt;
            row.UpdatedAt = resolvedAt;
            changed++;
        }

        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }

    public async Task<bool> ResolveAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string semanticKey,
        DateTimeOffset resolvedAt,
        CancellationToken ct)
    {
        var row = await db.FinancialSignals.SingleOrDefaultAsync(x =>
            x.UserId == userId &&
            x.FullWorthSpaceId == fullWorthSpaceId &&
            x.SemanticKey == semanticKey, ct);
        if (row is null) return false;
        if (row.ResolvedAt.HasValue) return true;

        row.ResolvedAt = resolvedAt;
        row.UpdatedAt = resolvedAt;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<FinancialSignalView>> ListAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string view,
        int limit,
        DateTimeOffset now,
        CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var normalizedView = NormalizeView(view);

        var query = db.FinancialSignals.AsNoTracking()
            .Where(x => x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId);

        query = normalizedView == "resolved"
            ? query.Where(x => x.ResolvedAt != null || (x.ValidUntil != null && x.ValidUntil <= now))
            : query.Where(x => x.ResolvedAt == null && (x.ValidUntil == null || x.ValidUntil > now));

        var rows = await query
            .OrderByDescending(x => x.RankScore)
            .ThenByDescending(x => x.UpdatedAt)
            .Take(Math.Min(400, limit * 4))
            .ToListAsync(ct);

        var states = await LoadStatesAsync(userId, rows.Select(x => x.Id), ct);
        return rows
            .Select(row => ToView(row, states.GetValueOrDefault(row.Id), now))
            .Where(viewRow => normalizedView switch
            {
                "active" => viewRow.State is not FinancialSignalStates.Dismissed &&
                            !(viewRow.State == FinancialSignalStates.Snoozed && viewRow.SnoozedUntil > now),
                "hidden" => viewRow.State == FinancialSignalStates.Dismissed ||
                            (viewRow.State == FinancialSignalStates.Snoozed && viewRow.SnoozedUntil > now),
                _ => true
            })
            .Take(limit)
            .ToList();
    }

    public async Task<FinancialSignalView?> GetAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid signalId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var row = await db.FinancialSignals.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == signalId && x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (row is null) return null;

        var state = await db.FinancialSignalStates.AsNoTracking().SingleOrDefaultAsync(x =>
            x.SignalId == row.Id && x.UserId == userId, ct);
        return ToView(row, state, now);
    }

    public Task<bool> MarkReadAsync(Guid userId, Guid fullWorthSpaceId, Guid signalId, CancellationToken ct) =>
        SetStateAsync(userId, fullWorthSpaceId, signalId, FinancialSignalStates.Read, null, ct);

    public Task<bool> DismissAsync(Guid userId, Guid fullWorthSpaceId, Guid signalId, CancellationToken ct) =>
        SetStateAsync(userId, fullWorthSpaceId, signalId, FinancialSignalStates.Dismissed, null, ct);

    public Task<bool> SnoozeAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid signalId,
        DateTimeOffset until,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (until <= now || until > now.AddYears(1))
            throw new ArgumentOutOfRangeException(nameof(until), "Snooze must be in the future and at most one year.");
        return SetStateAsync(userId, fullWorthSpaceId, signalId, FinancialSignalStates.Snoozed, until, ct);
    }

    public async Task<bool> SetFeedbackAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid signalId,
        string feedback,
        CancellationToken ct)
    {
        var normalized = feedback?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || !FinancialSignalFeedback.IsValid(normalized))
            throw new ArgumentException("Feedback must be useful or irrelevant.", nameof(feedback));

        if (!await SignalExistsAsync(userId, fullWorthSpaceId, signalId, ct)) return false;
        var state = await GetOrCreateStateAsync(userId, signalId, ct);
        state.Feedback = normalized;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> SetStateAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid signalId,
        string stateValue,
        DateTimeOffset? snoozedUntil,
        CancellationToken ct)
    {
        if (!FinancialSignalStates.IsValid(stateValue))
            throw new ArgumentException("Invalid signal state.", nameof(stateValue));
        if (!await SignalExistsAsync(userId, fullWorthSpaceId, signalId, ct)) return false;

        var state = await GetOrCreateStateAsync(userId, signalId, ct);
        state.State = stateValue;
        state.SnoozedUntil = stateValue == FinancialSignalStates.Snoozed ? snoozedUntil : null;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private Task<bool> SignalExistsAsync(Guid userId, Guid fullWorthSpaceId, Guid signalId, CancellationToken ct) =>
        db.FinancialSignals.AsNoTracking().AnyAsync(x =>
            x.Id == signalId && x.UserId == userId && x.FullWorthSpaceId == fullWorthSpaceId, ct);

    private async Task<FinancialSignalState> GetOrCreateStateAsync(Guid userId, Guid signalId, CancellationToken ct)
    {
        var state = await db.FinancialSignalStates.SingleOrDefaultAsync(x =>
            x.SignalId == signalId && x.UserId == userId, ct);
        if (state is not null) return state;

        state = new FinancialSignalState
        {
            SignalId = signalId,
            UserId = userId,
            State = FinancialSignalStates.Unread,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.FinancialSignalStates.Add(state);
        return state;
    }

    private async Task<Dictionary<Guid, FinancialSignalState>> LoadStatesAsync(
        Guid userId,
        IEnumerable<Guid> signalIds,
        CancellationToken ct)
    {
        var ids = signalIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        return await db.FinancialSignalStates.AsNoTracking()
            .Where(x => x.UserId == userId && ids.Contains(x.SignalId))
            .ToDictionaryAsync(x => x.SignalId, ct);
    }

    private static FinancialSignalView ToView(
        FinancialSignal row,
        FinancialSignalState? state,
        DateTimeOffset now)
    {
        var effectiveState = state?.State ?? FinancialSignalStates.Unread;
        var snoozedUntil = state?.SnoozedUntil;
        if (effectiveState == FinancialSignalStates.Snoozed && snoozedUntil <= now)
        {
            effectiveState = FinancialSignalStates.Unread;
            snoozedUntil = null;
        }

        return new FinancialSignalView(
            row.Id,
            row.FullWorthSpaceId,
            row.UserId,
            row.Type,
            row.SubjectType,
            row.SubjectId,
            row.SemanticKey,
            row.Source,
            row.Severity,
            row.Confidence,
            row.ImpactAmount,
            row.ImpactCurrency,
            row.TitleKey,
            ParseJson(row.PayloadJson),
            ParseJson(row.EvidenceJson),
            row.RankScore,
            row.DetectedAt,
            row.UpdatedAt,
            row.ValidUntil,
            row.ResolvedAt,
            row.Version,
            effectiveState,
            snoozedUntil,
            state?.Feedback);
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool JsonEquivalent(string left, string right)
    {
        using var leftDocument = JsonDocument.Parse(left);
        using var rightDocument = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
    }

    private static string NormalizeView(string? view) =>
        string.IsNullOrWhiteSpace(view) ? "active" : view.Trim().ToLowerInvariant() switch
        {
            "active" => "active",
            "resolved" => "resolved",
            "hidden" => "hidden",
            _ => throw new ArgumentException("View must be active, resolved or hidden.", nameof(view))
        };

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length is < 3 or > 8) throw new ArgumentException("Invalid impact currency.");
        return normalized;
    }

    private static void Validate(DetectedFinancialSignal detected)
    {
        if (detected.FullWorthSpaceId == Guid.Empty) throw new ArgumentException("FullWorth Space is required.");
        if (detected.UserId == Guid.Empty) throw new ArgumentException("User is required.");
        Required(detected.Type, 80, nameof(detected.Type));
        Required(detected.SubjectType, 80, nameof(detected.SubjectType));
        Required(detected.SubjectId, 160, nameof(detected.SubjectId));
        Required(detected.SemanticKey, 300, nameof(detected.SemanticKey));
        Required(detected.Source, 40, nameof(detected.Source));
        Required(detected.Severity, 24, nameof(detected.Severity));
        Required(detected.TitleKey, 160, nameof(detected.TitleKey));
        if (!FinancialSignalSeverities.IsValid(detected.Severity.Trim().ToLowerInvariant()))
            throw new ArgumentException("Signal severity must be info, attention or high.");
        if (detected.Confidence is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(detected.Confidence), "Confidence must be between 0 and 1.");
        _ = NormalizeCurrency(detected.ImpactCurrency);
        RequireJsonObject(detected.PayloadJson, nameof(detected.PayloadJson));
        RequireJsonObject(detected.EvidenceJson, nameof(detected.EvidenceJson));
    }

    private static void Required(string? value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
        if (value.Trim().Length > maxLength) throw new ArgumentException($"{name} is too long.", name);
    }

    private static void RequireJsonObject(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"{name} must be a JSON object.", name);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"{name} must be valid JSON.", name, exception);
        }
    }
}
