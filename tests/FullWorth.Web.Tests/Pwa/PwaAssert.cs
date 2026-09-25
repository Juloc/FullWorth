namespace FullWorth.Web.Tests.Pwa;

/// <summary>
/// "Wird dieses Asset mit der App ausgeliefert?" - die Frage, die viele Oberflaechen-Tests stellen.
///
/// Bis #154 hiess die Antwort "es steht im Vorrat des Service Workers", und viele Tests fragten den
/// Vorrat. Den Vorrat gibt es nicht mehr: der Worker cacht nur noch die Hinweisseite ohne Verbindung
/// (siehe sw.js), weil ohne das HTML einer Seite keine offline oeffnet. Die Frage, die der Vorrat
/// immer vertreten hat, stellt dieser Helfer direkt: laedt eine Seite das Asset? Sie ist die
/// strengere - ein gelistetes, aber von nichts importiertes Modul fiel frueher durch kein Raster.
/// </summary>
internal static class PwaAssert
{
    /// <param name="asset">Der Pfad, auch mit den Anfuehrungszeichen, mit denen sw.js ihn einmal fuehrte.</param>
    public static void Ships(string asset)
    {
        var path = asset.Trim('\'', '"');
        Assert.True(WebSources.LoadedByAPage(path), $"{path} is loaded by no page, so the app never ships it.");
    }
}
