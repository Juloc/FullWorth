using FullWorth.Backend.Modules.Merchants;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record LocalCategorySemanticCandidate(Guid Id, string Key, string Name);

/// <summary>
/// Expands a FullWorth Space's local category-key map with verified cloud ontology semantics.
/// It never renames or rewrites a local category. Ambiguous aliases are deliberately ignored.
/// </summary>
public sealed class CloudOntologyResolver(IntelligenceDbContext db)
{
    private const decimal MinimumAliasConfidence = 0.75m;

    public async Task<IReadOnlyDictionary<string, Guid>> ExpandCategoryMapAsync(
        IReadOnlyList<LocalCategorySemanticCandidate> localCategories,
        string? country,
        CancellationToken ct)
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in localCategories)
        {
            if (!string.IsNullOrWhiteSpace(category.Key))
                result[category.Key.Trim()] = category.Id;
        }

        if (localCategories.Count == 0)
            return result;

        var normalizedCountry = NormalizeCountry(country);
        var redirects = await db.OfficialOntologyRedirects.AsNoTracking()
            .Where(x => x.EntityType == "category")
            .ToDictionaryAsync(x => x.FromCanonicalKey, x => x.ToCanonicalKey, StringComparer.Ordinal, ct);

        var activeKeys = (await db.OfficialOntologyEntities.AsNoTracking()
                .Where(x => x.EntityType == "category" && x.Status == "active")
                .Select(x => x.CanonicalKey)
                .ToListAsync(ct))
            .Select(Resolve)
            .ToHashSet(StringComparer.Ordinal);

        // If a local category already uses a formerly canonical key, make the approved target resolve to
        // that same local category. This preserves historical/local keys without mutating the category.
        var redirectCandidates = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var category in localCategories)
        {
            var key = category.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;
            var resolved = Resolve(key);
            if (string.Equals(resolved, key, StringComparison.OrdinalIgnoreCase)) continue;

            if (!redirectCandidates.TryGetValue(resolved, out var ids))
                redirectCandidates[resolved] = ids = [];
            ids.Add(category.Id);
        }
        foreach (var (canonicalKey, ids) in redirectCandidates)
        {
            var distinct = ids.Distinct().ToList();
            if (distinct.Count == 1 && !result.ContainsKey(canonicalKey))
                result[canonicalKey] = distinct[0];
        }

        var aliases = await db.OfficialOntologyAliases.AsNoTracking()
            .Where(x => x.EntityType == "category" &&
                        x.Confidence >= MinimumAliasConfidence &&
                        (x.Country == "GLOBAL" || x.Country == normalizedCountry))
            .ToListAsync(ct);

        var aliasesByName = aliases
            .GroupBy(x => x.NormalizedAlias, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => Resolve(x.CanonicalKey))
                    .Where(activeKeys.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToList(),
                StringComparer.Ordinal);

        var semanticMatches = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var category in localCategories)
        {
            var normalizedName = MerchantNormalization.Normalize(category.Name);
            if (normalizedName is null ||
                !aliasesByName.TryGetValue(normalizedName, out var canonicalKeys) ||
                canonicalKeys.Count != 1)
                continue;

            var canonicalKey = canonicalKeys[0];
            if (!semanticMatches.TryGetValue(canonicalKey, out var ids))
                semanticMatches[canonicalKey] = ids = [];
            ids.Add(category.Id);
        }

        foreach (var (canonicalKey, ids) in semanticMatches)
        {
            var distinct = ids.Distinct().ToList();
            if (distinct.Count == 1 && !result.ContainsKey(canonicalKey))
                result[canonicalKey] = distinct[0];
        }

        return result;

        string Resolve(string key)
        {
            var current = key.Trim().ToLowerInvariant();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var depth = 0; depth < 20 && seen.Add(current); depth++)
            {
                if (!redirects.TryGetValue(current, out var next) || string.IsNullOrWhiteSpace(next))
                    return current;
                current = next;
            }
            return current;
        }
    }

    private static string NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "GLOBAL";
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 2 && normalized.All(char.IsAsciiLetter)
            ? normalized
            : "GLOBAL";
    }
}
