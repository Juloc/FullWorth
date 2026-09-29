using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

/// <summary>
/// Die staerkste Form eines KI-Vorschlags: keine einzelne Haendler-Zuordnung, sondern eine echte,
/// editierbare <see cref="CategorizationRule"/> unter <c>/rules</c>. Wo eine
/// <c>merchant-category</c>-Zeile genau EINEN Haendlernamen trifft, greift eine Regel nach Muster,
/// Betragsfenster oder MCC - und die Klassifizierung braucht danach keine KI mehr, egal wie oft
/// dieselbe Frage sonst gestellt wuerde.
///
/// Implementiert <see cref="IIntelligenceSuggestionAcceptor"/>, die Schnittstelle gehoert
/// Intelligence - siehe dort fuer das Warum: Categories importiert Intelligence bereits
/// (<see cref="TransactionRuleEngine"/> liest dessen DTOs), der Rueckweg waere ein Zyklus, den
/// <c>ModuleBoundaryTests</c> dauerhaft verbietet. Verdrahtet wird diese Klasse in
/// <c>BackendApplication</c>, das zu keinem Modul gehoert.
///
/// Angelegt wird die Regel ueber <see cref="CategoryStore"/>, nicht per Hand hier - dieselbe
/// Pruefung (die Kategorie muss zum FullWorth Space gehoeren), derselbe Store wie ein von Hand
/// angelegtes Original.
/// </summary>
public sealed class CategorizationRuleSuggestionAcceptor(
    IntelligenceDbContext intelligenceDb,
    FullWorthDbContext financeDb,
    CategoryStore categories) : IIntelligenceSuggestionAcceptor
{
    public string SuggestionType => "categorization-rule";

    public async Task<IntelligenceSuggestionReviewResult> AcceptAsync(
        IntelligenceSuggestion suggestion, Guid actorUserId, CancellationToken ct)
    {
        if (suggestion.FullWorthSpaceId is null)
            return new(false, "invalid_suggestion_payload", suggestion);

        Payload payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(suggestion.ProposedPayloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Missing payload.");
        }
        catch (JsonException)
        {
            return new(false, "invalid_suggestion_payload", suggestion);
        }

        var name = payload.Name?.Trim();
        var matchField = payload.MatchField?.Trim();
        var matchMode = payload.MatchMode?.Trim();
        var direction = payload.Direction?.Trim().ToLowerInvariant();
        var categoryKey = payload.CategoryKey?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(matchField) ||
            string.IsNullOrWhiteSpace(matchMode) || direction is not ("income" or "expense" or "any") ||
            string.IsNullOrWhiteSpace(categoryKey))
            return new(false, "invalid_suggestion_payload", suggestion);

        var category = await financeDb.Categories.AsNoTracking().SingleOrDefaultAsync(x =>
            x.FullWorthSpaceId == suggestion.FullWorthSpaceId.Value &&
            x.Key == categoryKey &&
            !x.IsArchived, ct);
        if (category is null) return new(false, "category_not_found", suggestion);

        try
        {
            await categories.UpsertRuleForSpaceAsync(
                suggestion.FullWorthSpaceId.Value,
                id: null,
                new RuleWrite(
                    Name: name,
                    IsEnabled: true,
                    Priority: payload.Priority ?? 100,
                    Target: "transaction",
                    MatchField: matchField,
                    MatchMode: matchMode,
                    Pattern: payload.Pattern?.Trim() ?? string.Empty,
                    Direction: direction,
                    MinAmount: payload.MinAmount,
                    MaxAmount: payload.MaxAmount,
                    MerchantCategoryCode: payload.MerchantCategoryCode?.Trim(),
                    CategoryId: category.Id,
                    MarkAsTransfer: payload.MarkAsTransfer,
                    StopProcessing: payload.StopProcessing),
                ct);
        }
        catch (InvalidOperationException)
        {
            // CategoryStore prueft dieselbe Zugehoerigkeit noch einmal selbst - kann hier nur noch
            // greifen, wenn die Kategorie zwischen den beiden Abfragen archiviert wurde.
            return new(false, "category_not_found", suggestion);
        }

        suggestion.MarkAccepted(actorUserId, DateTimeOffset.UtcNow);
        await intelligenceDb.SaveChangesAsync(ct);
        return new(true, null, suggestion);
    }

    /// <summary>Derselbe Schnitt wie <see cref="RuleWrite"/>, aber ueber Kategorie-KEY statt -ID:
    /// ein Vorschlag kennt keine Datenbank-Id, nur den portablen Schluessel.</summary>
    private sealed record Payload(
        string? Name,
        string? MatchField,
        string? MatchMode,
        string? Pattern,
        string? Direction,
        decimal? MinAmount,
        decimal? MaxAmount,
        string? MerchantCategoryCode,
        string? CategoryKey,
        bool MarkAsTransfer,
        bool StopProcessing,
        int? Priority,
        string? EvidenceSummary);
}
