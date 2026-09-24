namespace FullWorth.Web.Tests;

/// <summary>
/// Die Quelldateien des Frontends, aus dem Arbeitsverzeichnis gelesen.
///
/// Warum das hier steht und nicht in jeder Testdatei noch einmal: seit #154 liegt das Markup einer
/// Seite nicht mehr unter <c>wwwroot/pages/…/page.html</c>, sondern als Razor-Seite unter
/// <c>Pages/…/Index.cshtml</c>. Beim Umzug der ersten elf Seiten scheiterten vier Tests an nichts
/// anderem als an diesem Pfad - jeder hatte seinen eigenen kleinen Leser, und jeder musste einzeln
/// nachgezogen werden. Ein Ort dafür genügt.
/// </summary>
public static class WebSources
{
    /// <summary>
    /// Das Markup einer Seite, z. B. <c>Page("Pension")</c> oder <c>Page("Settings/Intelligence")</c>.
    /// </summary>
    public static string Page(string name) =>
        File.ReadAllText(Path.Combine(
            new[] { Web(), "Pages" }.Concat(name.Split('/')).Append("Index.cshtml").ToArray()));

    /// <summary>Eine Datei unter <c>wwwroot</c>, z. B. <c>Asset("pages", "pension", "entry.js")</c>.</summary>
    public static string Asset(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Web(), "wwwroot" }.Concat(parts).ToArray()));

    /// <summary>
    /// Der Rahmen, den jede Seite teilt: Kopf, Stilkette, Topbar, Coach-Dock, Toast.
    ///
    /// Das stand bis zum Ende von #154 in <c>wwwroot/index.html</c>. Das Dokument gibt es nicht mehr —
    /// was darin für ALLE Seiten galt, steht jetzt hier, und was nur eine Seite betraf, bei ihr.
    /// </summary>
    public static string Layout() => Shared("_Layout.cshtml");

    /// <summary>Die Seitenleiste. Trug in der alten Hülle die erzeugte <c>nav</c>-Sektion.</summary>
    public static string Navigation() => Shared("_Navigation.cshtml");

    /// <summary>Die untere Leiste am Telefon.</summary>
    public static string BottomNavigation() => Shared("_BottomNavigation.cshtml");

    /// <summary>Eine Quelldatei des Web-Projekts, z. B. <c>Source("Navigation", "PageHeadings.cs")</c>.</summary>
    public static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { Web() }.Concat(parts).ToArray()));

    private static string Shared(string name) =>
        File.ReadAllText(Path.Combine(Web(), "Pages", "Shared", name));

    /// <summary>
    /// Die Wurzel des Arbeitsbaums. Ein Test, der ueber das Web-Projekt hinaussieht - etwa in die
    /// aufgezeichnete Routenflaeche -, braucht sie; sie noch einmal zu suchen waere der zweite Leser,
    /// den diese Klasse gerade abschafft.
    /// </summary>
    public static string RepoRoot => Root();

    private static string Web() => Path.Combine(Root(), "src", "FullWorth.Web");

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("FullWorth.slnx nicht gefunden.");
        return directory.FullName;
    }
}
