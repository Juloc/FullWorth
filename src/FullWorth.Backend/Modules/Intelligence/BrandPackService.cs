using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Modules.Merchants;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record VerifiedBrandBlob(
    string ContentSha256,
    string MediaType,
    int ByteLength,
    string ContentBase64);

public static class BrandAssetVerifier
{
    public const int MaximumAssetBytes = FullWorthCloudClient.MaximumBrandAssetBytes;

    public static VerifiedBrandBlob VerifySvg(
        byte[] bytes,
        string? mediaType,
        string? expectedSha256 = null,
        int expectedByteLength = 0)
    {
        var normalizedMediaType = string.IsNullOrWhiteSpace(mediaType)
            ? "image/svg+xml"
            : mediaType.Trim().ToLowerInvariant();
        if (normalizedMediaType != "image/svg+xml" || bytes.Length is <= 0 or > MaximumAssetBytes)
            throw new KnowledgePackVerificationException("knowledge_pack_brand_asset_invalid");

        if (expectedByteLength > 0 && bytes.Length != expectedByteLength)
            throw new KnowledgePackVerificationException("knowledge_pack_brand_asset_size_mismatch");

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var expected = expectedSha256.Trim().ToLowerInvariant();
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                throw new KnowledgePackVerificationException("knowledge_pack_brand_asset_invalid");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualHash),
                    Encoding.ASCII.GetBytes(expected)))
                throw new KnowledgePackVerificationException("knowledge_pack_brand_asset_hash_mismatch");
        }

        string svg;
        try { svg = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            throw new KnowledgePackVerificationException("knowledge_pack_brand_asset_invalid");
        }

        var lowered = svg.ToLowerInvariant();
        if (!lowered.Contains("<svg", StringComparison.Ordinal) ||
            lowered.Contains("<script", StringComparison.Ordinal) ||
            lowered.Contains("<foreignobject", StringComparison.Ordinal) ||
            lowered.Contains("javascript:", StringComparison.Ordinal) ||
            lowered.Contains("onload=", StringComparison.Ordinal) ||
            lowered.Contains("onerror=", StringComparison.Ordinal) ||
            lowered.Contains("<iframe", StringComparison.Ordinal) ||
            lowered.Contains("<object", StringComparison.Ordinal) ||
            lowered.Contains("<embed", StringComparison.Ordinal) ||
            lowered.Contains("href=\"http", StringComparison.Ordinal) ||
            lowered.Contains("href='http", StringComparison.Ordinal) ||
            lowered.Contains("url(http", StringComparison.Ordinal) ||
            lowered.Contains("xlink:href=", StringComparison.Ordinal))
            throw new KnowledgePackVerificationException("knowledge_pack_brand_svg_unsafe");

        return new VerifiedBrandBlob(
            actualHash,
            normalizedMediaType,
            bytes.Length,
            Convert.ToBase64String(bytes));
    }

    public static string NormalizeBrandKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new KnowledgePackVerificationException("knowledge_pack_brand_asset_invalid");
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 120 ||
            !normalized.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
            throw new KnowledgePackVerificationException("knowledge_pack_brand_asset_invalid");
        return normalized;
    }

    public static string NormalizeAlias(string? value)
    {
        var normalized = MerchantNormalization.Normalize(value);
        if (normalized is null || normalized.Length > 300)
            throw new KnowledgePackVerificationException("knowledge_pack_brand_alias_invalid");
        return normalized;
    }

    public static string NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "GLOBAL";
        var normalized = value.Trim().ToUpperInvariant();
        return normalized == "GLOBAL" ||
               (normalized.Length == 2 && normalized.All(char.IsAsciiLetter))
            ? normalized
            : "GLOBAL";
    }

    public static string? NormalizeSourceUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > 1000 ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new KnowledgePackVerificationException("knowledge_pack_brand_asset_invalid");
        return trimmed;
    }
}

public sealed record CustomBrandAssetImport(
    string BrandKey,
    string CanonicalName,
    string? LogoKey,
    string? MediaType,
    string ContentBase64,
    string? ContentSha256,
    string? SourceName,
    string? SourceUrl,
    string? LicenseNote);

public sealed record CustomBrandAliasImport(
    string AliasKey,
    string BrandKey,
    string? Country);

public sealed record CustomBrandPackImportRequest(
    string Name,
    string? Version,
    int? Priority,
    bool? Enabled,
    IReadOnlyList<CustomBrandAssetImport>? Assets,
    IReadOnlyList<CustomBrandAliasImport>? Aliases);

public sealed record CustomBrandPackView(
    Guid Id,
    string Name,
    string Version,
    int Priority,
    bool Enabled,
    int AssetCount,
    int AliasCount,
    DateTimeOffset UpdatedAt);

public sealed record BrandCatalogAssetView(
    string BrandKey,
    string CanonicalName,
    string LogoKey,
    string DataUri,
    string ContentSha256,
    string? SourceName,
    string? SourceUrl,
    string? LicenseNote,
    string Source,
    int Priority);

public sealed record BrandCatalogAliasView(
    string AliasKey,
    string BrandKey,
    string Country,
    string Source,
    int Priority);

public sealed class BrandPackService(IntelligenceDbContext db)
{
    private static readonly TimeSpan UnreferencedBlobRetention = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<CustomBrandPackView>> ListCustomPacksAsync(CancellationToken ct)
    {
        var packs = await db.CustomBrandPacks.AsNoTracking()
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Name)
            .ToListAsync(ct);
        var assets = await db.CustomBrandAssets.AsNoTracking()
            .GroupBy(x => x.PackId)
            .Select(g => new { PackId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PackId, x => x.Count, ct);
        var aliases = await db.CustomBrandAliases.AsNoTracking()
            .GroupBy(x => x.PackId)
            .Select(g => new { PackId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PackId, x => x.Count, ct);

        return packs.Select(x => new CustomBrandPackView(
            x.Id,
            x.Name,
            x.Version,
            x.Priority,
            x.Enabled,
            assets.GetValueOrDefault(x.Id),
            aliases.GetValueOrDefault(x.Id),
            x.UpdatedAt)).ToList();
    }

    public async Task<CustomBrandPackView> ImportCustomPackAsync(
        CustomBrandPackImportRequest request,
        CancellationToken ct)
    {
        var name = TrimRequired(request.Name, 120, "brand_pack_name_invalid");
        var version = string.IsNullOrWhiteSpace(request.Version) ? "1" : TrimRequired(request.Version, 80, "brand_pack_version_invalid");
        var priority = Math.Clamp(request.Priority ?? 1000, 1, 10_000);
        var enabled = request.Enabled ?? true;
        var inputs = request.Assets ?? [];
        var aliasesInput = request.Aliases ?? [];
        if (inputs.Count is < 1 or > 5_000 || aliasesInput.Count > 100_000)
            throw new ArgumentException("brand_pack_size_invalid");

        var preparedAssets = new List<(CustomBrandAsset Asset, VerifiedBrandBlob Blob)>(inputs.Count);
        var brandKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            var brandKey = BrandAssetVerifier.NormalizeBrandKey(input.BrandKey);
            if (!brandKeys.Add(brandKey))
                throw new ArgumentException("brand_pack_duplicate_brand");
            var canonicalName = TrimRequired(input.CanonicalName, 200, "brand_pack_brand_name_invalid");
            var logoKey = string.IsNullOrWhiteSpace(input.LogoKey)
                ? brandKey
                : BrandAssetVerifier.NormalizeBrandKey(input.LogoKey);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(input.ContentBase64 ?? string.Empty); }
            catch (FormatException) { throw new ArgumentException("brand_pack_asset_base64_invalid"); }

            VerifiedBrandBlob blob;
            try
            {
                blob = BrandAssetVerifier.VerifySvg(
                    bytes,
                    input.MediaType,
                    input.ContentSha256,
                    0);
            }
            catch (KnowledgePackVerificationException ex)
            {
                throw new ArgumentException(ex.ErrorCode);
            }

            preparedAssets.Add((
                new CustomBrandAsset
                {
                    BrandKey = brandKey,
                    CanonicalName = canonicalName,
                    LogoKey = logoKey,
                    MediaType = blob.MediaType,
                    ContentSha256 = blob.ContentSha256,
                    ByteLength = blob.ByteLength,
                    SourceName = Trim(input.SourceName, 200),
                    SourceUrl = NormalizeSourceUrlForImport(input.SourceUrl),
                    LicenseNote = Trim(input.LicenseNote, 500)
                },
                blob));
        }

        var aliases = new List<CustomBrandAlias>(aliasesInput.Count);
        var aliasKeys = new HashSet<(string Alias, string Country)>();
        foreach (var input in aliasesInput)
        {
            string alias;
            string brandKey;
            try
            {
                alias = BrandAssetVerifier.NormalizeAlias(input.AliasKey);
                brandKey = BrandAssetVerifier.NormalizeBrandKey(input.BrandKey);
            }
            catch (KnowledgePackVerificationException ex)
            {
                throw new ArgumentException(ex.ErrorCode);
            }
            if (!brandKeys.Contains(brandKey))
                throw new ArgumentException("brand_pack_alias_orphan");
            var country = BrandAssetVerifier.NormalizeCountry(input.Country);
            if (!aliasKeys.Add((alias, country)))
                throw new ArgumentException("brand_pack_duplicate_alias");
            aliases.Add(new CustomBrandAlias
            {
                AliasKey = alias,
                BrandKey = brandKey,
                Country = country
            });
        }

        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var pack = await db.CustomBrandPacks.SingleOrDefaultAsync(x => x.Name == name, ct);
        if (pack is null)
        {
            pack = new CustomBrandPack { Name = name, CreatedAt = now };
            db.CustomBrandPacks.Add(pack);
        }
        else
        {
            db.CustomBrandAliases.RemoveRange(await db.CustomBrandAliases.Where(x => x.PackId == pack.Id).ToListAsync(ct));
            db.CustomBrandAssets.RemoveRange(await db.CustomBrandAssets.Where(x => x.PackId == pack.Id).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }

        pack.Version = version;
        pack.Priority = priority;
        pack.Enabled = enabled;
        pack.UpdatedAt = now;

        var hashes = preparedAssets.Select(x => x.Blob.ContentSha256).Distinct().ToArray();
        var existing = await db.BrandAssetBlobs
            .Where(x => hashes.Contains(x.ContentSha256))
            .ToDictionaryAsync(x => x.ContentSha256, StringComparer.Ordinal, ct);
        foreach (var prepared in preparedAssets)
        {
            if (existing.TryGetValue(prepared.Blob.ContentSha256, out var cached))
            {
                cached.LastUsedAt = now;
            }
            else
            {
                var blob = new BrandAssetBlob
                {
                    ContentSha256 = prepared.Blob.ContentSha256,
                    MediaType = prepared.Blob.MediaType,
                    ByteLength = prepared.Blob.ByteLength,
                    ContentBase64 = prepared.Blob.ContentBase64,
                    CreatedAt = now,
                    LastUsedAt = now
                };
                db.BrandAssetBlobs.Add(blob);
                existing[prepared.Blob.ContentSha256] = blob;
            }

            prepared.Asset.PackId = pack.Id;
            db.CustomBrandAssets.Add(prepared.Asset);
        }
        foreach (var alias in aliases)
        {
            alias.PackId = pack.Id;
            db.CustomBrandAliases.Add(alias);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await PruneUnreferencedBlobsAsync(ct);

        return (await ListCustomPacksAsync(ct)).Single(x => x.Id == pack.Id);
    }

    public async Task<bool> SetCustomPackEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var pack = await db.CustomBrandPacks.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (pack is null) return false;
        pack.Enabled = enabled;
        pack.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteCustomPackAsync(Guid id, CancellationToken ct)
    {
        var pack = await db.CustomBrandPacks.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (pack is null) return false;
        db.CustomBrandAliases.RemoveRange(await db.CustomBrandAliases.Where(x => x.PackId == id).ToListAsync(ct));
        db.CustomBrandAssets.RemoveRange(await db.CustomBrandAssets.Where(x => x.PackId == id).ToListAsync(ct));
        db.CustomBrandPacks.Remove(pack);
        await db.SaveChangesAsync(ct);
        await PruneUnreferencedBlobsAsync(ct);
        return true;
    }

    public async Task<(IReadOnlyList<BrandCatalogAssetView> Assets, IReadOnlyList<BrandCatalogAliasView> Aliases)>
        GetEffectiveCatalogAsync(CancellationToken ct)
    {
        var blobs = await db.BrandAssetBlobs.AsNoTracking().ToDictionaryAsync(x => x.ContentSha256, StringComparer.Ordinal, ct);
        var officialAssets = await db.OfficialBrandAssets.AsNoTracking().ToListAsync(ct);
        var officialAliases = await db.OfficialBrandAliases.AsNoTracking().ToListAsync(ct);

        var packs = await db.CustomBrandPacks.AsNoTracking()
            .Where(x => x.Enabled)
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.UpdatedAt)
            .ToListAsync(ct);
        var packIds = packs.Select(x => x.Id).ToArray();
        var customAssets = packIds.Length == 0
            ? []
            : await db.CustomBrandAssets.AsNoTracking().Where(x => packIds.Contains(x.PackId)).ToListAsync(ct);
        var customAliases = packIds.Length == 0
            ? []
            : await db.CustomBrandAliases.AsNoTracking().Where(x => packIds.Contains(x.PackId)).ToListAsync(ct);

        var assets = new Dictionary<string, BrandCatalogAssetView>(StringComparer.Ordinal);
        foreach (var pack in packs)
        {
            foreach (var asset in customAssets.Where(x => x.PackId == pack.Id))
            {
                if (assets.ContainsKey(asset.BrandKey) || !blobs.TryGetValue(asset.ContentSha256, out var blob))
                    continue;
                assets[asset.BrandKey] = ToView(asset, blob, $"custom:{pack.Name}", pack.Priority);
            }
        }
        foreach (var asset in officialAssets)
        {
            if (assets.ContainsKey(asset.BrandKey) || !blobs.TryGetValue(asset.ContentSha256, out var blob))
                continue;
            assets[asset.BrandKey] = ToView(asset, blob, "official", 0);
        }

        var aliases = new List<BrandCatalogAliasView>();
        foreach (var pack in packs)
            aliases.AddRange(customAliases.Where(x => x.PackId == pack.Id)
                .Select(x => new BrandCatalogAliasView(x.AliasKey, x.BrandKey, x.Country, $"custom:{pack.Name}", pack.Priority)));
        aliases.AddRange(officialAliases.Select(x =>
            new BrandCatalogAliasView(x.AliasKey, x.BrandKey, x.Country, "official", 0)));

        return (
            assets.Values.OrderByDescending(x => x.Priority).ThenBy(x => x.BrandKey).ToList(),
            aliases.OrderByDescending(x => x.Priority).ThenByDescending(x => x.AliasKey.Length).ThenBy(x => x.AliasKey).ToList());
    }

    public async Task PruneUnreferencedBlobsAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - UnreferencedBlobRetention;
        var official = await db.OfficialBrandAssets.AsNoTracking().Select(x => x.ContentSha256).ToListAsync(ct);
        var custom = await db.CustomBrandAssets.AsNoTracking().Select(x => x.ContentSha256).ToListAsync(ct);
        var referenced = official.Concat(custom).ToHashSet(StringComparer.Ordinal);
        var old = await db.BrandAssetBlobs.Where(x => x.LastUsedAt < cutoff).ToListAsync(ct);
        var remove = old.Where(x => !referenced.Contains(x.ContentSha256)).ToList();
        if (remove.Count == 0) return;
        db.BrandAssetBlobs.RemoveRange(remove);
        await db.SaveChangesAsync(ct);
    }

    private static BrandCatalogAssetView ToView(
        CustomBrandAsset asset,
        BrandAssetBlob blob,
        string source,
        int priority) =>
        new(
            asset.BrandKey,
            asset.CanonicalName,
            asset.LogoKey,
            $"data:{blob.MediaType};base64,{blob.ContentBase64}",
            asset.ContentSha256,
            asset.SourceName,
            asset.SourceUrl,
            asset.LicenseNote,
            source,
            priority);

    private static BrandCatalogAssetView ToView(
        OfficialBrandAsset asset,
        BrandAssetBlob blob,
        string source,
        int priority) =>
        new(
            asset.BrandKey,
            asset.CanonicalName,
            asset.LogoKey,
            $"data:{blob.MediaType};base64,{blob.ContentBase64}",
            asset.ContentSha256,
            asset.SourceName,
            asset.SourceUrl,
            asset.LicenseNote,
            source,
            priority);

    private static string TrimRequired(string? value, int max, string error)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(error);
        var trimmed = value.Trim();
        if (trimmed.Length > max) throw new ArgumentException(error);
        return trimmed;
    }

    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string? NormalizeSourceUrlForImport(string? value)
    {
        try { return BrandAssetVerifier.NormalizeSourceUrl(value); }
        catch (KnowledgePackVerificationException ex) { throw new ArgumentException(ex.ErrorCode); }
    }
}
