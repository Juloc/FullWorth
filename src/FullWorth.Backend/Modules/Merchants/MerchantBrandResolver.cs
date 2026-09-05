using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Merchants;

/// <summary>
/// Resolved presentation identity for a transaction counterparty: which registry merchant it maps to
/// (via alias), the merchant's display name, and the local brand visuals (logo/accent). Every field is
/// nullable; when no brand is known the frontend degrades to the transaction's category icon (§4).
/// </summary>
public sealed record MerchantBrandIdentity(
    Guid? MerchantId,
    string? MerchantDisplayName,
    string? BrandKey,
    string? LogoAssetPath,
    string? AccentKey)
{
    public static readonly MerchantBrandIdentity None = new(null, null, null, null, null);
}

/// <summary>Merchant row reduced to what brand resolution needs (registry override + name for curated match).</summary>
public sealed record MerchantBrandRow(
    Guid Id,
    string Name,
    string NormalizedName,
    string? BrandKey,
    string? LogoAssetPath,
    string? AccentKey,
    bool BrandOverridden);

/// <summary>
/// Deterministic alias → merchant → brand resolution for ONE FullWorth Space, built from an in-memory
/// snapshot so it can run inside the transaction-list projection and analytics without N+1 queries.
/// Precedence: (1) longest matching space alias picks the registry merchant; (2) that merchant's brand is
/// its explicit override when set, otherwise a curated local-catalog match on its canonical name; (3) if
/// no merchant matched, a curated match is attempted directly on the counterparty. Unknown → all brand
/// fields null (category-icon fallback). This is the single resolver the transaction DTO relies on so the
/// browser never re-runs normalization (§4/§7).
/// </summary>
public sealed class MerchantBrandResolver
{
    private readonly IReadOnlyList<(string Alias, Guid MerchantId)> aliases; // longest-first
    private readonly IReadOnlyDictionary<Guid, MerchantBrandRow> merchantsById;

    public MerchantBrandResolver(IEnumerable<(string Alias, Guid MerchantId)> aliasRows, IEnumerable<MerchantBrandRow> merchantRows)
    {
        aliases = aliasRows
            .Where(row => !string.IsNullOrEmpty(row.Alias))
            .OrderByDescending(row => row.Alias.Length)
            .ToList();
        merchantsById = merchantRows.ToDictionary(row => row.Id);
    }

    /// <summary>Load a per-space resolver snapshot (aliases + merchants with their brand fields).</summary>
    public static async Task<MerchantBrandResolver> ForSpaceAsync(FullWorthDbContext db, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var resolvers = await ForSpacesAsync(db, [fullWorthSpaceId], ct);
        return resolvers[fullWorthSpaceId];
    }

    /// <summary>One resolver per requested space; empty spaces still get an (empty) resolver.</summary>
    public static async Task<IReadOnlyDictionary<Guid, MerchantBrandResolver>> ForSpacesAsync(
        FullWorthDbContext db, IReadOnlyCollection<Guid> fullWorthSpaceIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, MerchantBrandResolver>();
        if (fullWorthSpaceIds.Count == 0) return result;

        var aliasRows = await db.Set<MerchantAlias>().AsNoTracking()
            .Where(alias => fullWorthSpaceIds.Contains(alias.FullWorthSpaceId))
            .Select(alias => new { alias.FullWorthSpaceId, alias.NormalizedAlias, alias.MerchantId })
            .ToListAsync(ct);
        var merchantRows = await db.Set<Merchant>().AsNoTracking()
            .Where(merchant => fullWorthSpaceIds.Contains(merchant.FullWorthSpaceId))
            .Select(merchant => new
            {
                merchant.FullWorthSpaceId,
                Row = new MerchantBrandRow(merchant.Id, merchant.Name, merchant.NormalizedName,
                    merchant.BrandKey, merchant.LogoAssetPath, merchant.AccentKey, merchant.BrandOverridden)
            })
            .ToListAsync(ct);

        var aliasBySpace = aliasRows
            .GroupBy(row => row.FullWorthSpaceId)
            .ToDictionary(group => group.Key, group => group.Select(row => (row.NormalizedAlias, row.MerchantId)).ToList());
        var merchantsBySpace = merchantRows
            .GroupBy(row => row.FullWorthSpaceId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.Row).ToList());

        foreach (var spaceId in fullWorthSpaceIds.Distinct())
            result[spaceId] = new MerchantBrandResolver(
                aliasBySpace.TryGetValue(spaceId, out var a) ? a : [],
                merchantsBySpace.TryGetValue(spaceId, out var m) ? m : []);
        return result;
    }

    public MerchantBrandIdentity Resolve(string? counterparty, string? normalizedCounterparty = null)
    {
        var normalized = !string.IsNullOrWhiteSpace(normalizedCounterparty)
            ? normalizedCounterparty.Trim().ToUpperInvariant()
            : MerchantNormalization.Normalize(counterparty);
        if (string.IsNullOrEmpty(normalized)) return MerchantBrandIdentity.None;

        // (1) longest alias contained in the counterparty picks the registry merchant.
        MerchantBrandRow? merchant = null;
        foreach (var (alias, merchantId) in aliases)
        {
            if (normalized.Contains(alias, StringComparison.Ordinal) && merchantsById.TryGetValue(merchantId, out var row))
            {
                merchant = row;
                break;
            }
        }

        if (merchant is not null)
        {
            // (2) merchant brand: explicit override wins (even a cleared/empty override means "no brand"),
            // otherwise a curated match on the merchant's canonical name, then on the raw counterparty.
            if (merchant.BrandOverridden)
                return new MerchantBrandIdentity(merchant.Id, merchant.Name, merchant.BrandKey, merchant.LogoAssetPath, merchant.AccentKey);

            var curated = LocalBrandCatalog.Match(merchant.NormalizedName) ?? LocalBrandCatalog.Match(normalized);
            return new MerchantBrandIdentity(merchant.Id, merchant.Name, curated?.BrandKey, curated?.LogoAssetPath, curated?.AccentKey);
        }

        // (3) no registry merchant — try a curated brand directly on the counterparty.
        var direct = LocalBrandCatalog.Match(normalized);
        return direct is null
            ? MerchantBrandIdentity.None
            : new MerchantBrandIdentity(null, null, direct.BrandKey, direct.LogoAssetPath, direct.AccentKey);
    }
}

/// <summary>
/// Small curated set of local brand visuals. Match tokens are compared as whole words against a
/// <see cref="MerchantNormalization"/>-normalized counterparty; the longest token wins so specific brands
/// beat generic ones. Logo paths are app-local references the frontend maps to bundled assets; unknown
/// counterparties simply return null and fall back to the category icon (§4).
/// </summary>
public static class LocalBrandCatalog
{
    public sealed record BrandDefinition(string BrandKey, string LogoAssetPath, string? AccentKey, IReadOnlyList<string> MatchTokens);

    // brandKey → definition. Accent keys are stable design tokens (frontend maps token → colour).
    private static readonly IReadOnlyList<BrandDefinition> Brands = Build(
        ("rewe", ["REWE"]),
        ("aldi", ["ALDI", "ALDI SUED", "ALDI NORD"]),
        ("lidl", ["LIDL"]),
        ("edeka", ["EDEKA"]),
        ("netto", ["NETTO", "NETTO MARKEN DISCOUNT"]),
        ("kaufland", ["KAUFLAND"]),
        ("dm", ["DM DROGERIE", "DM FILIALE"]),
        ("rossmann", ["ROSSMANN"]),
        ("amazon", ["AMAZON", "AMZN", "AMAZON MKTPL", "AMAZON PRIME"]),
        ("paypal", ["PAYPAL"]),
        ("netflix", ["NETFLIX"]),
        ("spotify", ["SPOTIFY"]),
        ("apple", ["APPLE", "APPLE COM", "ITUNES"]),
        ("google", ["GOOGLE"]),
        ("ikea", ["IKEA"]),
        ("mcdonalds", ["MCDONALDS", "MC DONALDS"]),
        ("starbucks", ["STARBUCKS"]),
        ("shell", ["SHELL"]),
        ("aral", ["ARAL"]),
        ("db", ["DEUTSCHE BAHN", "DB VERTRIEB", "DB BAHN"]),
        ("telekom", ["TELEKOM", "DEUTSCHE TELEKOM"]),
        ("vodafone", ["VODAFONE"]));

    // Longest tokens first so a specific multi-word brand beats a shorter generic token.
    private static readonly IReadOnlyList<(string Token, BrandDefinition Brand)> TokensByLength =
        Brands.SelectMany(brand => brand.MatchTokens.Select(token => (token, brand)))
            .OrderByDescending(pair => pair.token.Length)
            .ToList();

    /// <summary>Curated brand (brand fields only, no merchant identity) for a normalized counterparty, or null.</summary>
    public static MerchantBrandIdentity? Match(string? normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        var padded = $" {normalized.Trim().ToUpperInvariant()} ";
        foreach (var (token, brand) in TokensByLength)
            if (padded.Contains($" {token} ", StringComparison.Ordinal))
                return new MerchantBrandIdentity(null, null, brand.BrandKey, brand.LogoAssetPath, brand.AccentKey);
        return null;
    }

    /// <summary>True when the brand key is one of the curated local brands (used to validate overrides).</summary>
    public static bool IsKnownBrand(string? brandKey) =>
        !string.IsNullOrWhiteSpace(brandKey) && Brands.Any(brand => string.Equals(brand.BrandKey, brandKey.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string? DefaultLogoAssetPath(string? brandKey) =>
        Brands.FirstOrDefault(brand => string.Equals(brand.BrandKey, brandKey?.Trim(), StringComparison.OrdinalIgnoreCase))?.LogoAssetPath;

    public static string? DefaultAccentKey(string? brandKey) =>
        Brands.FirstOrDefault(brand => string.Equals(brand.BrandKey, brandKey?.Trim(), StringComparison.OrdinalIgnoreCase))?.AccentKey;

    private static IReadOnlyList<BrandDefinition> Build(params (string BrandKey, string[] Tokens)[] rows) =>
        rows.Select(row => new BrandDefinition(
            row.BrandKey,
            $"brands/{row.BrandKey}.svg",
            $"brand-{row.BrandKey}",
            row.Tokens)).ToList();
}
