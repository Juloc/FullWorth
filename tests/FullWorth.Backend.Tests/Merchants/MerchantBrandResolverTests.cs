using FullWorth.Backend.Modules.Merchants;

namespace FullWorth.Backend.Tests.Merchants;

/// <summary>
/// Pure-unit coverage for §4 alias → merchant → brand resolution: a curated brand is auto-detected, an
/// explicit merchant override wins, and an unknown counterparty degrades to null brand fields (so the
/// frontend falls back to the category icon). No database required.
/// </summary>
public sealed class MerchantBrandResolverTests
{
    [Fact]
    public void RegistryMerchant_AutoDetectsCuratedBrand()
    {
        var merchantId = Guid.NewGuid();
        var resolver = new MerchantBrandResolver(
            [("REWE", merchantId)],
            [new MerchantBrandRow(merchantId, "REWE", "REWE", null, null, null, BrandOverridden: false)]);

        var identity = resolver.Resolve("REWE SAGT DANKE 1234");

        Assert.Equal(merchantId, identity.MerchantId);
        Assert.Equal("REWE", identity.MerchantDisplayName);
        Assert.Equal("rewe", identity.BrandKey);
        Assert.Equal("brands/rewe.svg", identity.LogoAssetPath);
        Assert.Equal("brand-rewe", identity.AccentKey);
    }

    [Fact]
    public void ExplicitOverride_WinsOverCuratedDetection()
    {
        var merchantId = Guid.NewGuid();
        // Merchant name would auto-detect "rewe", but the override forces a custom brand.
        var resolver = new MerchantBrandResolver(
            [("REWE", merchantId)],
            [new MerchantBrandRow(merchantId, "REWE", "REWE", "my-brand", "assets/custom.svg", "brand-custom", BrandOverridden: true)]);

        var identity = resolver.Resolve("REWE CITY 42");

        Assert.Equal("my-brand", identity.BrandKey);
        Assert.Equal("assets/custom.svg", identity.LogoAssetPath);
        Assert.Equal("brand-custom", identity.AccentKey);
    }

    [Fact]
    public void ClearedOverride_MeansNoBrandEvenIfNameWouldMatch()
    {
        var merchantId = Guid.NewGuid();
        // BrandOverridden=true with all-null fields is an intentional "no brand — use the category icon".
        var resolver = new MerchantBrandResolver(
            [("NETFLIX", merchantId)],
            [new MerchantBrandRow(merchantId, "Netflix", "NETFLIX", null, null, null, BrandOverridden: true)]);

        var identity = resolver.Resolve("NETFLIX.COM AMSTERDAM");

        Assert.Equal(merchantId, identity.MerchantId);
        Assert.Null(identity.BrandKey);
        Assert.Null(identity.LogoAssetPath);
    }

    [Fact]
    public void UnknownCounterparty_DegradesToNullBrand()
    {
        var resolver = new MerchantBrandResolver([], []);

        var identity = resolver.Resolve("SOME LOCAL CORNER SHOP");

        Assert.Equal(MerchantBrandIdentity.None, identity);
        Assert.Null(identity.MerchantId);
        Assert.Null(identity.BrandKey);
    }

    [Fact]
    public void DirectCuratedMatch_WorksWithoutRegistryMerchant()
    {
        var resolver = new MerchantBrandResolver([], []);

        var identity = resolver.Resolve("SPOTIFY P0ABC STOCKHOLM");

        Assert.Null(identity.MerchantId);
        Assert.Equal("spotify", identity.BrandKey);
        Assert.Equal("brands/spotify.svg", identity.LogoAssetPath);
    }

    [Fact]
    public void CuratedMatch_IsWholeWord_NotSubstring()
    {
        // "ARAL" must not match inside "PHARMACY"/"GENERALI" etc.
        Assert.Null(LocalBrandCatalog.Match("GENERALI VERSICHERUNG"));
        Assert.NotNull(LocalBrandCatalog.Match("ARAL TANKSTELLE 55"));
    }

    [Fact]
    public void LongestTokenWins_AcrossBrands()
    {
        // "NETTO MARKEN DISCOUNT" should resolve to netto, not accidentally to a shorter token.
        var identity = LocalBrandCatalog.Match("NETTO MARKEN DISCOUNT SAGT DANKE");
        Assert.NotNull(identity);
        Assert.Equal("netto", identity!.BrandKey);
    }
}
