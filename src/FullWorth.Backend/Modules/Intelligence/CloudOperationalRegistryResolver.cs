using FullWorth.Backend.Modules.Merchants;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record CloudContractProviderIdentity(
    string ProviderKey,
    string CanonicalName,
    string? ProviderCategory,
    string Country,
    string? BrandKey,
    decimal Confidence);

public sealed record CloudProductIdentity(
    string ProductKey,
    string CanonicalName,
    string? CategoryKey,
    string Country,
    string? BrandKey,
    decimal Confidence);

/// <summary>
/// Resolves reviewed cloud identities from the last verified signed knowledge pack only.
/// No network lookup is performed here, so classification keeps working while FullWorth Cloud is offline.
/// </summary>
public sealed class CloudOperationalRegistryResolver(IntelligenceDbContext db)
{
    public async Task<CloudContractProviderIdentity?> ResolveProviderAsync(
        string? providerOrCounterparty,
        string? country,
        CancellationToken ct)
    {
        var normalized = MerchantNormalization.Normalize(providerOrCounterparty);
        if (normalized is null) return null;

        var normalizedCountry = NormalizeCountry(country);
        var signatures = await db.OfficialContractSignatures.AsNoTracking()
            .Where(x => x.MerchantFingerprint == normalized && x.Confidence >= 0.80m)
            .OrderByDescending(x => x.Confidence)
            .ToListAsync(ct);

        var providerKeys = signatures.Select(x => x.ProviderKey).Distinct(StringComparer.Ordinal).ToArray();
        var providers = providerKeys.Length == 0
            ? new List<OfficialContractProvider>()
            : await db.OfficialContractProviders.AsNoTracking()
                .Where(x => providerKeys.Contains(x.ProviderKey))
                .ToListAsync(ct);

        var byKey = providers.ToDictionary(x => x.ProviderKey, StringComparer.Ordinal);
        var exact = signatures
            .Where(x => byKey.TryGetValue(x.ProviderKey, out var p) &&
                        (p.Country == "GLOBAL" || normalizedCountry == "GLOBAL" || p.Country == normalizedCountry))
            .Select(x => new { Signature = x, Provider = byKey[x.ProviderKey] })
            .OrderByDescending(x => x.Provider.Country == normalizedCountry)
            .ThenByDescending(x => x.Signature.Confidence)
            .ToList();

        if (exact.Count > 0)
        {
            var top = exact[0];
            var ambiguous = exact.Skip(1).Any(x =>
                x.Signature.Confidence == top.Signature.Confidence &&
                x.Provider.Country == top.Provider.Country &&
                !string.Equals(x.Provider.ProviderKey, top.Provider.ProviderKey, StringComparison.Ordinal));
            if (!ambiguous)
                return ToIdentity(top.Provider, top.Signature.Confidence);
        }

        var aliasCandidates = await db.OfficialOntologyAliases.AsNoTracking()
            .Where(x => x.EntityType == "provider" &&
                        x.NormalizedAlias == normalized &&
                        x.Confidence >= 0.80m)
            .OrderByDescending(x => x.Country == normalizedCountry)
            .ThenByDescending(x => x.Confidence)
            .ToListAsync(ct);
        if (aliasCandidates.Count == 0) return null;

        var resolvedAliases = new List<(OfficialContractProvider Provider, decimal Confidence, bool ExactCountry)>();
        foreach (var alias in aliasCandidates)
        {
            var key = await ResolveRedirectAsync("provider", alias.CanonicalKey, ct);
            var provider = await db.OfficialContractProviders.AsNoTracking()
                .SingleOrDefaultAsync(x =>
                    x.ProviderKey == key &&
                    (x.Country == "GLOBAL" || normalizedCountry == "GLOBAL" || x.Country == normalizedCountry), ct);
            if (provider is not null)
                resolvedAliases.Add((provider, alias.Confidence, provider.Country == normalizedCountry));
        }

        var bestAliases = resolvedAliases
            .OrderByDescending(x => x.ExactCountry)
            .ThenByDescending(x => x.Confidence)
            .ToList();
        if (bestAliases.Count == 0) return null;
        var best = bestAliases[0];
        if (bestAliases.Skip(1).Any(x =>
                x.ExactCountry == best.ExactCountry &&
                x.Confidence == best.Confidence &&
                !string.Equals(x.Provider.ProviderKey, best.Provider.ProviderKey, StringComparison.Ordinal)))
            return null;

        return ToIdentity(best.Provider, best.Confidence);
    }

    public async Task<CloudProductIdentity?> ResolveProductByGtinAsync(string? gtin, CancellationToken ct)
    {
        if (!GtinKey.TryCreateGtinSubjectKey(gtin, out var subjectKey) ||
            string.IsNullOrWhiteSpace(subjectKey))
            return null;

        var normalized = subjectKey["gtin:".Length..];
        var link = await db.OfficialProductGtins.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Gtin == normalized, ct);
        if (link is null) return null;

        var key = await ResolveRedirectAsync("product", link.ProductKey, ct);
        var product = await db.OfficialProducts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProductKey == key, ct);
        return product is null ? null : ToIdentity(product, 1m);
    }

    public async Task<CloudProductIdentity?> ResolveProductAliasAsync(
        string? alias,
        string? merchantContext,
        string? country,
        CancellationToken ct)
    {
        var normalizedAlias = MerchantNormalization.Normalize(alias);
        if (normalizedAlias is null) return null;
        var normalizedMerchant = MerchantNormalization.Normalize(merchantContext);
        var normalizedCountry = NormalizeCountry(country);

        var candidates = await db.OfficialProductAliases.AsNoTracking()
            .Where(x => x.AliasKey == normalizedAlias && x.Confidence >= 0.80m)
            .OrderByDescending(x => x.MerchantContext == normalizedMerchant && normalizedMerchant != null)
            .ThenByDescending(x => x.MerchantContext == null)
            .ThenByDescending(x => x.Confidence)
            .Take(10)
            .ToListAsync(ct);

        var resolvedProducts = new List<(OfficialProduct Product, decimal Confidence, int ContextRank)>();
        foreach (var candidate in candidates)
        {
            if (candidate.MerchantContext is not null &&
                !string.Equals(candidate.MerchantContext, normalizedMerchant, StringComparison.Ordinal))
                continue;

            var key = await ResolveRedirectAsync("product", candidate.ProductKey, ct);
            var product = await db.OfficialProducts.AsNoTracking()
                .SingleOrDefaultAsync(x =>
                    x.ProductKey == key &&
                    (x.Country == "GLOBAL" || normalizedCountry == "GLOBAL" || x.Country == normalizedCountry), ct);
            if (product is null) continue;

            var contextRank = candidate.MerchantContext is not null &&
                              string.Equals(candidate.MerchantContext, normalizedMerchant, StringComparison.Ordinal)
                ? 2
                : 1;
            resolvedProducts.Add((product, candidate.Confidence, contextRank));
        }

        var bestProducts = resolvedProducts
            .OrderByDescending(x => x.ContextRank)
            .ThenByDescending(x => x.Confidence)
            .ToList();
        if (bestProducts.Count == 0) return null;
        var bestProduct = bestProducts[0];
        if (bestProducts.Skip(1).Any(x =>
                x.ContextRank == bestProduct.ContextRank &&
                x.Confidence == bestProduct.Confidence &&
                !string.Equals(x.Product.ProductKey, bestProduct.Product.ProductKey, StringComparison.Ordinal)))
            return null;

        return ToIdentity(bestProduct.Product, bestProduct.Confidence);
    }

    private async Task<string> ResolveRedirectAsync(string entityType, string canonicalKey, CancellationToken ct)
    {
        var current = canonicalKey;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 20 && seen.Add(current); depth++)
        {
            var redirect = await db.OfficialOntologyRedirects.AsNoTracking()
                .SingleOrDefaultAsync(x =>
                    x.EntityType == entityType &&
                    x.FromCanonicalKey == current, ct);
            if (redirect is null) return current;
            current = redirect.ToCanonicalKey;
        }
        return current;
    }

    private static CloudContractProviderIdentity ToIdentity(OfficialContractProvider provider, decimal confidence) =>
        new(
            provider.ProviderKey,
            provider.CanonicalName,
            provider.ProviderCategory,
            provider.Country,
            provider.BrandKey,
            confidence);

    private static CloudProductIdentity ToIdentity(OfficialProduct product, decimal confidence) =>
        new(
            product.ProductKey,
            product.CanonicalName,
            product.CategoryKey,
            product.Country,
            product.BrandKey,
            confidence);

    private static string NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "GLOBAL";
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 2 && normalized.All(char.IsAsciiLetter)
            ? normalized
            : "GLOBAL";
    }
}
