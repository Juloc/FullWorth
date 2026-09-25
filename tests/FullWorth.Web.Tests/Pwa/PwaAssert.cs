namespace FullWorth.Web.Tests.Pwa;

/// <summary>
/// "Wird dieses Asset mit der App ausgeliefert?" - die Frage, die viele Oberflaechen-Tests stellen, seit
/// es den Service Worker gibt.
///
/// Bis #154 hiess die Antwort immer "es steht im Vorrat des Service Workers". Seit Abschnitt 12 gilt
/// das nur noch fuer globale Assets: eine Seite steht nicht mehr im Vorrat, weil der Worker ihr HTML nie
/// cacht und ihr JS offline deshalb nichts gerettet haette. Eine Seitendatei gehoert zur App, wenn eine
/// Seite sie laedt - dann landet sie beim ersten Besuch im Cache. Die Tests, die vorher den Vorrat
/// fragten, fragen jetzt hier; die Frage wurde nicht weicher, sondern strenger: ein gelistetes, aber von
/// nichts importiertes Modul fiel frueher durch kein Raster.
/// </summary>
internal static class PwaAssert
{
    /// <param name="asset">Der Pfad, mit oder ohne die Anfuehrungszeichen, mit denen sw.js ihn fuehrt.</param>
    /// <param name="serviceWorker">Der Inhalt von sw.js.</param>
    public static void Ships(string asset, string serviceWorker)
    {
        var path = asset.Trim('\'', '"');
        if (path.StartsWith("/pages/", StringComparison.Ordinal))
            Assert.True(WebSources.LoadedByAPage(path), $"{path} is loaded by no page, so the app never ships it.");
        else
            Assert.Contains($"'{path}'", serviceWorker, StringComparison.Ordinal);
    }
}
