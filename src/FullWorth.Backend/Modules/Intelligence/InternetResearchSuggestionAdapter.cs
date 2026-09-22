using System.Text.Json;
using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Macht aus einer Netz-Recherche das, was FullWorth schon kennt (#176).
///
/// Die Recherche beantwortet eine Frage - was fuer ein Geschaeft ist das - und diese Antwort hat zwei
/// Empfaenger, die es beide schon gibt:
///
/// <list type="bullet">
///   <item><c>merchant-category</c> fuer einen Haendler, den bisher niemand einordnen konnte.</item>
///   <item><c>contract-enrichment</c> fuer den Anbieter eines wiederkehrenden Betrags.</item>
/// </list>
///
/// Beide Typen haben bereits einen Pruefweg, eine Oberflaeche und - beim Haendler - die Rueckmeldung
/// an das deterministische System. Ein eigener Weg fuer "aus dem Netz" waere ein zweiter davon, und
/// der waere der, der irgendwann abweicht. Die Herkunft steht deshalb nur im Nachweis: welche Adresse
/// gelesen wurde und was dort stand.
///
/// Recherchiert wird ausschliesslich, was sonst niemand weiss. Ein Haendler mit Kategorie, ein
/// Haendler mit offenem Vorschlag und ein Haendler, den die geplante Kategorisierung gerade selbst
/// eingeordnet hat, kosten hier nichts - sonst waere die teuerste Funktion die, die am meisten
/// doppelt macht.
/// </summary>
public sealed class InternetResearchSuggestionAdapter(
    FullWorthDbContext financeDb,
    IntelligenceDbContext intelligenceDb,
    IntelligenceStore store,
    InternetResearchService research,
    ILogger<InternetResearchSuggestionAdapter> logger)
{
    /// <summary>So viele Namen bekommt ein Lauf - der naechste nimmt die naechsten.</summary>
    public const int PerRun = 10;

    /// <summary>Unter so vielen Buchungen lohnt kein Nachschlag: der Haendler kommt kaum vor.</summary>
    private const int MinimumOccurrences = 3;

    public async Task<int> ResearchUnknownMerchantsAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var categoryKeys = await financeDb.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId && !category.IsArchived)
            .Select(category => category.Key)
            .ToArrayAsync(ct);

        // Haendler ohne Kategorie, haeufigste zuerst. Ein Vorschlag dort ist am meisten wert, und die
        // Zahl der Buchungen ist das einzige Mass, das ohne weitere Abfrage zu haben ist.
        var candidates = await financeDb.Transactions.AsNoTracking()
            .Where(transaction =>
                transaction.NormalizedCounterparty != null && transaction.NormalizedCounterparty != "" &&
                transaction.CategoryId == null && !transaction.IsIgnored && !transaction.IsTransfer &&
                financeDb.Accounts.Any(account =>
                    account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId))
            .GroupBy(transaction => transaction.NormalizedCounterparty!)
            .Where(group => group.Count() >= MinimumOccurrences)
            .OrderByDescending(group => group.Count())
            .Select(group => new { Merchant = group.Key, Occurrences = group.Count() })
            .Take(PerRun * 4)
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        // Wer schon einen offenen Vorschlag hat, braucht keinen zweiten.
        var merchants = candidates.Select(candidate => candidate.Merchant).ToArray();
        var alreadySuggested = await intelligenceDb.IntelligenceSuggestions.AsNoTracking()
            .Where(suggestion =>
                suggestion.FullWorthSpaceId == fullWorthSpaceId &&
                suggestion.Type == "merchant-category" &&
                suggestion.Status == IntelligenceSuggestionStatuses.Pending &&
                merchants.Contains(suggestion.SubjectId))
            .Select(suggestion => suggestion.SubjectId)
            .ToArrayAsync(ct);
        var skip = alreadySuggested.ToHashSet(StringComparer.Ordinal);

        var written = 0;
        foreach (var candidate in candidates)
        {
            if (written >= PerRun) break;
            ct.ThrowIfCancellationRequested();
            if (skip.Contains(candidate.Merchant)) continue;

            var described = await research.DescribeAsync(candidate.Merchant, null, categoryKeys, ct);
            // Nicht freigegeben oder Anbieter nicht erreichbar: der naechste Name hilft auch nicht.
            if (described.Outcome is InternetResearchService.OutcomeNoAccess
                or InternetResearchService.OutcomeProviderFailed
                or InternetResearchService.OutcomeBudget) break;
            if (described.Outcome != InternetResearchService.OutcomeOk) continue;
            if (described.CategoryKey is null) continue;

            await store.TryAddSuggestionAsync(new IntelligenceSuggestion
            {
                FullWorthSpaceId = fullWorthSpaceId,
                Type = "merchant-category",
                SubjectType = "merchant",
                SubjectId = candidate.Merchant,
                // Ausgaben: ein Haendler ohne Kategorie ist in dieser Auswahl das, wofuer Geld
                // ausgegeben wurde - eine Einnahme traegt ihre Richtung nicht aus einer Webseite.
                SemanticKey = "merchant-category:expense",
                ProposedPayloadJson = JsonSerializer.Serialize(new
                {
                    categoryKey = described.CategoryKey,
                    direction = "expense",
                    evidenceSummary = described.Summary
                }),
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    source = "internet-research",
                    described.Domain,
                    described.Url,
                    described.Kind,
                    candidate.Occurrences
                }),
                Provider = "internet-research",
                Model = string.Empty,
                Confidence = 0.6m
            }, ct);
            written++;
        }

        if (written > 0) logger.LogInformation("Internet research proposed {Count} merchant categories.", written);
        return written;
    }
}
