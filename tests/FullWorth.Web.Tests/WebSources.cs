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
