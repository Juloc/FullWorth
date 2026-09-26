namespace FullWorth.Web.Tests;

/// <summary>
/// Eigene Markenpakete sind die Stufe, mit der ein Betreiber ein Logo selbst bestimmt - sie
/// ueberschreibt den mitgelieferten Katalog und die eigene Recherche. Diese Datei hiess
/// IntelligenceCloudUiBaselineTests und pruefte daneben die Cloud-Zustimmung; der Rest ist mit der
/// Cloud verschwunden, dieser Teil nicht.
/// </summary>
public sealed class IntelligenceBrandPackUiBaselineTests
{
    [Fact]
    public void Intelligence_page_exposes_local_custom_brand_pack_management()
    {
        var html = WebSources.Page("Settings/Intelligence");
        var script = WebSources.Asset("pages", "settings", "intelligence", "brand-packs.js");

        Assert.Contains("Eigene Brand-Packs", html);
        Assert.Contains("brand-pack-file", html);
        Assert.Contains("brand-pack-import", html);
        Assert.Contains("contentBase64", html);
        // brand-packs.js now delegates through the shared BFF client (core/services.js -> core/api.js),
        // which prefixes the path itself, so the literal path here has no leading slash.
        Assert.Contains("api/intelligence/admin/brand-packs/custom", script);
        Assert.Contains("/enabled", script);
        Assert.Contains("method: 'DELETE'", script);
        Assert.Contains("20 * 1024 * 1024", script);
    }
}
