using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Brands;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Der mitgelieferte Markenkatalog wird von einem Skript erzeugt, das keine SVGs prueft - absichtlich,
/// eine zweite Pruefung in JavaScript waere eine Kopie, die auseinanderlaeuft. Geprueft wird hier,
/// mit denselben Validatoren wie ein fremder Import, offline, bei jedem Testlauf.
/// </summary>
public sealed class BundledBrandCatalogTests
{
    [Fact]
    public void Every_bundled_asset_matches_its_manifest_entry_and_survives_the_real_verifier()
    {
        Assert.NotEmpty(BundledBrandCatalog.Brands);

        foreach (var brand in BundledBrandCatalog.Brands)
        {
            var content = BundledBrandCatalog.Content(brand);

            // Laenge, Hash, Medientyp und die SVG-Haertung in einem Aufruf - genau der Weg, den auch
            // ein selbst hochgeladenes Markenpaket geht.
            var verified = BrandAssetVerifier.VerifySvg(
                content, "image/svg+xml", brand.ContentSha256, brand.ByteLength);

            Assert.Equal(brand.ContentSha256, verified.ContentSha256);
            Assert.Equal(brand.ByteLength, content.Length);
        }
    }

    /// <summary>
    /// Die Umlaut-Falle. BrandAliasKey.Of faltet Diakritika, der Browser in ux-kit.js auch - ein
    /// Alias, der mit "Ü" im Katalog steht, findet seinen Haendler NIE, und nichts schlaegt dabei
    /// fehl: das Logo ist einfach nicht da. Nur dieser Test faengt das.
    /// </summary>
    [Fact]
    public void Every_alias_is_already_in_BrandAliasKey_form()
    {
        foreach (var brand in BundledBrandCatalog.Brands)
        foreach (var alias in brand.Aliases)
            Assert.Equal(alias, BrandAliasKey.Of(alias));
    }

    [Fact]
    public void No_alias_is_claimed_by_two_brands_and_no_brand_is_without_one()
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var brand in BundledBrandCatalog.Brands)
        {
            Assert.NotEmpty(brand.Aliases);
            foreach (var alias in brand.Aliases)
            {
                Assert.False(
                    owner.TryGetValue(alias, out var other),
                    $"\"{alias}\" gehoert {other} und {brand.BrandKey} - welches Logo eine Buchung " +
                    "bekaeme, entschiede dann die Reihenfolge.");
                owner[alias] = brand.BrandKey;
            }
        }
    }

    /// <summary>
    /// Eine Quelle, eine Lizenz. Gemischte Herkunft ist genau das, was man nicht auf hundert Marken
    /// hochskaliert: bei einem Einspruch muesste man sonst je Logo nachsehen, woher es kam.
    /// </summary>
    [Fact]
    public void Every_brand_names_one_source_one_licence_and_a_pinned_url()
    {
        foreach (var brand in BundledBrandCatalog.Brands)
        {
            Assert.Equal("simple-icons/simple-icons", brand.SourceName);
            Assert.Equal(
                "Simple Icons CC0-1.0; trademarks remain property of their owners.",
                brand.LicenseNote);
            Assert.Contains($"simple-icons@{BundledBrandCatalog.SimpleIconsVersion}/", brand.SourceUrl);
            Assert.EndsWith($"/{brand.BrandKey}.svg", brand.SourceUrl, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Die Ratsche gegen "vierzig Haendler ergaenzt, kein Logo dazu". Sie prueft nicht, dass jeder
    /// Haendler ein Logo hat - Simple Icons fuehrt die deutschen Handelsmarken schlicht nicht -,
    /// sondern dass der Bestand nicht schrumpft.
    /// </summary>
    [Fact]
    public void The_catalog_does_not_shrink()
    {
        Assert.True(
            BundledBrandCatalog.Brands.Count >= 95,
            $"Nur noch {BundledBrandCatalog.Brands.Count} Marken. Wurde ops/brand-icons/build.mjs " +
            "mit einem Netzfehler ausgefuehrt und das Ergebnis eingecheckt?");
    }
}
