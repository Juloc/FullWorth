using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record IntelligenceSuggestionReviewResult(bool Success, string? ErrorCode, IntelligenceSuggestion? Suggestion);

/// <summary>
/// Uebernimmt einen Vorschlagstyp, dessen Annahme etwas ausserhalb von Intelligence anlegt - eine
/// echte <c>CategorizationRule</c> zum Beispiel, die in Modules/Categories wohnt.
///
/// Diese Schnittstelle gehoert Intelligence, nicht die Implementierung: Categories importiert
/// Intelligence bereits (<c>TransactionRuleEngine</c> liest dessen DTOs), und der Rueckweg
/// (Intelligence importiert Categories) waere ein Zyklus, den <c>ModuleBoundaryTests</c>
/// dauerhaft verbietet - "ein Eintrag darf verschwinden, keiner darf dazukommen". Die
/// Implementierung lebt deshalb im fremden Modul und wird ueber DI verdrahtet
/// (<c>BackendApplication</c>, das zu keinem Modul gehoert); Intelligence kennt nur diese
/// Schnittstelle und den Vorschlag, nie die Kategorie-Regel dahinter.
/// </summary>
public interface IIntelligenceSuggestionAcceptor
{
    /// <summary>Der Vorschlagstyp, den dieser Acceptor uebernimmt - z. B. "categorization-rule".</summary>
    string SuggestionType { get; }

    Task<IntelligenceSuggestionReviewResult> AcceptAsync(
        IntelligenceSuggestion suggestion, Guid actorUserId, CancellationToken ct);
}

public sealed class IntelligenceSuggestionReviewService(
    IntelligenceDbContext intelligenceDb,
    FullWorthDbContext financeDb,
    IntelligenceFeedbackRecorder feedback,
    IEnumerable<IIntelligenceSuggestionAcceptor> acceptors)
{
    public Task<List<IntelligenceSuggestion>> ListPendingAsync(int limit, CancellationToken ct) =>
        intelligenceDb.IntelligenceSuggestions.AsNoTracking()
            .Where(x => x.Status == IntelligenceSuggestionStatuses.Pending)
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.CreatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

    public async Task<IntelligenceSuggestionReviewResult> AcceptAsync(Guid suggestionId, Guid actorUserId, CancellationToken ct)
    {
        var suggestion = await intelligenceDb.IntelligenceSuggestions.SingleOrDefaultAsync(x => x.Id == suggestionId, ct);
        if (suggestion is null) return new(false, "suggestion_not_found", null);
        if (suggestion.Status != IntelligenceSuggestionStatuses.Pending) return new(false, "suggestion_not_pending", suggestion);

        if (string.Equals(suggestion.Type, "merchant-category", StringComparison.Ordinal) &&
            string.Equals(suggestion.SubjectType, "merchant", StringComparison.Ordinal))
            return await AcceptMerchantCategoryAsync(suggestion, actorUserId, ct);

        if (suggestion.Type is "product-normalization" or "receipt-follow-up" or "contract-enrichment")
            return await AcceptReviewedProposalAsync(suggestion, actorUserId, ct);

        var acceptor = acceptors.FirstOrDefault(x => string.Equals(x.SuggestionType, suggestion.Type, StringComparison.Ordinal));
        if (acceptor is not null) return await acceptor.AcceptAsync(suggestion, actorUserId, ct);

        return new(false, "unsupported_suggestion_type", suggestion);
    }

    private async Task<IntelligenceSuggestionReviewResult> AcceptMerchantCategoryAsync(
        IntelligenceSuggestion suggestion,
        Guid actorUserId,
        CancellationToken ct)
    {
        if (suggestion.FullWorthSpaceId is null)
            return new(false, "invalid_suggestion_payload", suggestion);

        MerchantCategorySuggestionPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<MerchantCategorySuggestionPayload>(suggestion.ProposedPayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Missing payload.");
        }
        catch (JsonException)
        {
            return new(false, "invalid_suggestion_payload", suggestion);
        }

        var normalizedCounterparty = suggestion.SubjectId.Trim();
        var direction = payload.Direction?.Trim().ToLowerInvariant();
        var categoryKey = payload.CategoryKey?.Trim();
        if (normalizedCounterparty.Length is < 1 or > 320 || direction is not ("income" or "expense") || string.IsNullOrWhiteSpace(categoryKey))
            return new(false, "invalid_suggestion_payload", suggestion);

        var category = await financeDb.Categories.AsNoTracking().SingleOrDefaultAsync(x =>
            x.FullWorthSpaceId == suggestion.FullWorthSpaceId.Value &&
            x.Key == categoryKey &&
            !x.IsArchived, ct);
        if (category is null) return new(false, "category_not_found", suggestion);

        var mapping = await intelligenceDb.LearnedMerchantMappings.SingleOrDefaultAsync(x =>
            x.FullWorthSpaceId == suggestion.FullWorthSpaceId.Value &&
            x.NormalizedCounterparty == normalizedCounterparty &&
            x.Direction == direction, ct);
        var now = DateTimeOffset.UtcNow;
        if (mapping is null)
        {
            mapping = new LearnedMerchantMapping
            {
                FullWorthSpaceId = suggestion.FullWorthSpaceId.Value,
                CreatedByUserId = actorUserId,
                NormalizedCounterparty = normalizedCounterparty,
                Direction = direction,
                CategoryId = category.Id,
                Source = "ai-confirmed",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            intelligenceDb.LearnedMerchantMappings.Add(mapping);
        }
        else
        {
            mapping.CategoryId = category.Id;
            mapping.CreatedByUserId = actorUserId;
            mapping.Source = "ai-confirmed";
            mapping.IsActive = true;
            mapping.UpdatedAt = now;
        }

        // "REWE ist Lebensmittel" gilt fuer jeden Haushalt auf dieser Instanz, nicht nur fuer den,
        // der gerade zugestimmt hat - anders als die LearnedMerchantMapping oben, die bewusst pro
        // Haushalt bleibt (ein Gegenkonto kann fuer den einen Urlaub, fuer den anderen Ausgabe
        // sein). Ohne diese Zeile war InstanceMerchantMappings seit der Cloud-Abschaffung ein
        // Nachschlagewerk ohne Schreiber: gelesen wird es laengst (TransactionRuleEngine), nur
        // schrieb niemand mehr hinein. 0,95, nicht die 0,55 einer unbestaetigten KI-Antwort und
        // auch nicht knapp ueber der Lese-Schwelle von 0,80 - ein Mensch hat zugestimmt, das ist
        // mehr wert als jede automatische Vermutung.
        var instanceMapping = await intelligenceDb.InstanceMerchantMappings.SingleOrDefaultAsync(x =>
            x.AliasKey == normalizedCounterparty && x.Direction == direction && x.Country == "GLOBAL", ct);
        if (instanceMapping is null)
            intelligenceDb.InstanceMerchantMappings.Add(new InstanceMerchantMapping
            {
                AliasKey = normalizedCounterparty,
                Direction = direction,
                CategoryKey = category.Key,
                Country = "GLOBAL",
                Confidence = 0.95m
            });
        else
        {
            instanceMapping.CategoryKey = category.Key;
            instanceMapping.Confidence = 0.95m;
        }

        suggestion.MarkAccepted(actorUserId, now);
        await intelligenceDb.SaveChangesAsync(ct);

        // Das Uebernehmen verbessert das deterministische System - die LearnedMerchantMapping oben -
        // UND wird als verallgemeinerbare Erkenntnis vermerkt. Genau dieser zweite Teil fehlte: die
        // Rueckmeldung wurde mit Generalizable=false geschrieben und blieb deshalb liegen, obwohl
        // "REWE ist Lebensmittel" fuer jeden gilt und kein persoenliches Datum enthaelt.
        //
        // Ueber denselben Recorder wie die Korrektur in den Buchungsdetails: dieselbe Eignungsregel,
        // dieselbe Projektion. Ein zweiter Weg wuerde frueher oder spaeter etwas anderes vermerken
        // als dieser.
        await feedback.RecordMerchantMappingConfirmedAsync(
            suggestion.FullWorthSpaceId.Value,
            actorUserId,
            normalizedCounterparty,
            direction,
            category.Key,
            category.Name,
            "ai_suggestion_accepted",
            ct);

        return new(true, null, suggestion);
    }

    /// <summary>
    /// Phase-3 product/receipt/contract proposals are reviewable, but accepting them does not mutate
    /// FullWorthDbContext. Their domain-specific application belongs to the existing purchase/receipt/
    /// contract workflows where stale/manual-protection rules can be enforced explicitly.
    /// </summary>
    private async Task<IntelligenceSuggestionReviewResult> AcceptReviewedProposalAsync(
        IntelligenceSuggestion suggestion,
        Guid actorUserId,
        CancellationToken ct)
    {
        if (!suggestion.FullWorthSpaceId.HasValue)
            return new(false, "invalid_suggestion_scope", suggestion);

        // Reject stale references for persisted product/receipt subjects. Contract-candidate subjects are
        // intentionally derived fingerprints rather than FinanceDb rows, so their scope is validated only.
        if (suggestion.Type == "product-normalization")
        {
            if (!Guid.TryParseExact(suggestion.SubjectId, "N", out var itemId))
                return new(false, "invalid_suggestion_payload", suggestion);
            var exists = await financeDb.PurchaseItems.AsNoTracking().AnyAsync(x =>
                x.Id == itemId && x.Purchase.FullWorthSpaceId == suggestion.FullWorthSpaceId.Value, ct);
            if (!exists) return new(false, "subject_not_found", suggestion);
        }
        else if (suggestion.Type == "receipt-follow-up")
        {
            if (!Guid.TryParseExact(suggestion.SubjectId, "N", out var documentId))
                return new(false, "invalid_suggestion_payload", suggestion);
            var exists = await financeDb.PurchaseDocuments.AsNoTracking().AnyAsync(x =>
                x.Id == documentId && x.Purchase.FullWorthSpaceId == suggestion.FullWorthSpaceId.Value, ct);
            if (!exists) return new(false, "subject_not_found", suggestion);
        }
        else
        {
            var scopeExists = await financeDb.FullWorthSpaces.AsNoTracking()
                .AnyAsync(x => x.Id == suggestion.FullWorthSpaceId.Value, ct);
            if (!scopeExists) return new(false, "subject_not_found", suggestion);
        }

        // Ensure the provider payload is at least syntactically valid JSON before recording approval.
        try
        {
            using var _ = JsonDocument.Parse(suggestion.ProposedPayloadJson);
        }
        catch (JsonException)
        {
            return new(false, "invalid_suggestion_payload", suggestion);
        }

        var now = DateTimeOffset.UtcNow;
        suggestion.MarkAccepted(actorUserId, now);
        intelligenceDb.IntelligenceFeedbackEvents.Add(new IntelligenceFeedbackEvent
        {
            FullWorthSpaceId = suggestion.FullWorthSpaceId.Value,
            UserId = actorUserId,
            EventType = "ai_suggestion_accepted",
            SubjectType = suggestion.SubjectType,
            SubjectId = suggestion.SubjectId,
            SubjectFingerprint = string.Empty,
            OldValueJson = "{}",
            NewValueJson = suggestion.ProposedPayloadJson,
            Source = "ai-review",
            Generalizable = false,
            CreatedAt = now
        });
        await intelligenceDb.SaveChangesAsync(ct);
        return new(true, null, suggestion);
    }

    public async Task<IntelligenceSuggestionReviewResult> RejectAsync(Guid suggestionId, Guid actorUserId, CancellationToken ct)
    {
        var suggestion = await intelligenceDb.IntelligenceSuggestions.SingleOrDefaultAsync(x => x.Id == suggestionId, ct);
        if (suggestion is null) return new(false, "suggestion_not_found", null);
        if (suggestion.Status != IntelligenceSuggestionStatuses.Pending) return new(false, "suggestion_not_pending", suggestion);

        suggestion.Status = IntelligenceSuggestionStatuses.Rejected;
        suggestion.ReviewedAt = DateTimeOffset.UtcNow;
        suggestion.ReviewedByUserId = actorUserId;
        if (suggestion.FullWorthSpaceId.HasValue)
        {
            intelligenceDb.IntelligenceFeedbackEvents.Add(new IntelligenceFeedbackEvent
            {
                FullWorthSpaceId = suggestion.FullWorthSpaceId.Value,
                UserId = actorUserId,
                EventType = "ai_suggestion_rejected",
                SubjectType = suggestion.SubjectType,
                SubjectId = suggestion.SubjectId,
                SubjectFingerprint = string.Empty,
                OldValueJson = suggestion.ProposedPayloadJson,
                NewValueJson = "{}",
                Source = "ai-review",
                Generalizable = false,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await intelligenceDb.SaveChangesAsync(ct);
        return new(true, null, suggestion);
    }

    private sealed record MerchantCategorySuggestionPayload(string? CategoryKey, string? Direction, string? EvidenceSummary);
}
