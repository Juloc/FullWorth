using System.Reflection;
using System.Text.Json;

namespace FullWorth.Backend.Modules.Intelligence.Brands;

/// <summary>Eine mitgelieferte Marke mit ihren Schreibweisen und ihrer Herkunft.</summary>
public sealed record BundledBrand(
    string BrandKey,
    string CanonicalName,
    string File,
    string ContentSha256,
    int ByteLength,
    IReadOnlyList<string> Aliases,
    string SourceName,
    string SourceUrl,
    string LicenseNote);

/// <summary>
/// Der Markenkatalog, der im Abbild steckt.
///
/// Vorher kamen die Logos aus einem signierten Wissenspaket der Cloud, das eine Instanz erst
/// herunterladen und pruefen musste - und ohne Cloud gar nicht bekam. Jetzt liegen sie in der
/// Assembly: hundert Marken, rund 140 kB, kein Netzzugriff, keine Anmeldung, kein Vertrauensanker.
/// Eine frisch installierte Instanz zeigt ihre Logos beim ersten Start.
///
/// Erzeugt wird der Inhalt von <c>ops/brand-icons/build.mjs</c> aus den Haendleraliasen des
/// GermanyCategorizationCatalog plus <c>extra-brands.json</c>. Er wird NICHT von Hand gepflegt;
/// wer eine Marke ergaenzen will, ergaenzt dort einen Alias und laesst das Skript laufen.
///
/// Geprueft werden die Dateien nicht hier, sondern in BundledBrandCatalogTests - mit denselben
/// Validatoren, die auch einen fremden Import pruefen. Eine zweite Pruefung in JavaScript waere
/// eine Kopie, die irgendwann auseinanderlaeuft.
/// </summary>
public static class BundledBrandCatalog
{
    private const string ManifestResource = "FullWorth.Backend.Modules.Intelligence.BrandAssets.catalog.json";
    private const string AssetPrefix = "FullWorth.Backend.Modules.Intelligence.BrandAssets.";

    private static readonly Lazy<Manifest> Loaded = new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Die Simple-Icons-Fassung, aus der dieser Katalog erzeugt wurde.</summary>
    public static string SimpleIconsVersion => Loaded.Value.SimpleIconsVersion;

    public static IReadOnlyList<BundledBrand> Brands => Loaded.Value.Brands;

    /// <summary>Die Bytes einer Marke. Wirft, wenn Manifest und Ressourcen auseinanderlaufen.</summary>
    public static byte[] Content(BundledBrand brand)
    {
        var assembly = typeof(BundledBrandCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(AssetPrefix + brand.File)
            ?? throw new InvalidOperationException(
                $"Der mitgelieferte Markenkatalog nennt {brand.File}, die Assembly enthaelt sie nicht.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static Manifest Read()
    {
        var assembly = typeof(BundledBrandCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ManifestResource)
            ?? throw new InvalidOperationException(
                "Der mitgelieferte Markenkatalog fehlt in der Assembly. ops/brand-icons/build.mjs laufen lassen.");
        return JsonSerializer.Deserialize<Manifest>(stream, Options)
            ?? throw new InvalidOperationException("Der mitgelieferte Markenkatalog ist leer.");
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private sealed record Manifest(string SimpleIconsVersion, IReadOnlyList<BundledBrand> Brands);
}
