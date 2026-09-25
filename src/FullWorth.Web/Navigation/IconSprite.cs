using Microsoft.AspNetCore.Components;

namespace FullWorth.Web.Navigation;

/// <summary>
/// Das eine SVG-Sprite der Oberflaeche (#154) und seine Adresse.
///
/// Die Adresse traegt den Fingerabdruck, den MapStaticAssets beim Build vergibt - das macht das Sprite
/// cache-sicher: ein Release mit geaenderten Symbolen nennt eine neue Adresse, und ein altes Sprite
/// aus dem Cache passt zu keiner. Die Abbildung stellt <c>WithStaticAssets()</c> als Metadatum an jede
/// Razor-Seite. Fehlt es (der Live-Modus des Dev-Stacks, der von der Platte liest), gilt der Name selbst.
/// </summary>
public static class IconSprite
{
    private const string Asset = "icons/sprite.svg";

    public static string Url(HttpContext context)
    {
        var assets = context.GetEndpoint()?.Metadata.GetMetadata<ResourceAssetCollection>();
        return "/" + (assets?[Asset] ?? Asset);
    }

    /// <summary>Der Verweis auf ein Symbol, fuer <c>&lt;use href&gt;</c>.</summary>
    public static string Href(HttpContext context, string symbol) => $"{Url(context)}#{symbol}";
}
