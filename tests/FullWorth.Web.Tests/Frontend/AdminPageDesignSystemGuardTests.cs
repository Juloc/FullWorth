using System.IO;
using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// Admin ist eine Seite der Anwendung.
///
/// Hier stand einmal das Gegenteil: "/admin is a separate shell on purpose — no sidebar, no bottom
/// nav". Das war der Grund, warum die Seite abdriftete — eigene ~40 Hex-Farben, ein eigener
/// Dunkelmodus über prefers-color-scheme, der die gewählte Einstellung überging, eigene Knöpfe,
/// eigene Dialoge, ein eigener Haltepunkt bei 850px. Nichts davon schlug fehl, es sah nur langsam
/// immer weniger nach der Anwendung aus. Und der Besitzer hatte keinen Weg zurück ins Seitenmenü.
///
/// Jetzt liegt sie unter pages/admin/ wie jede andere Seite, und was hier bleibt, ist die Frage, ob
/// sie sich etwas zurückholt, was ihr die Hülle schon gibt.
/// </summary>
public sealed class AdminPageDesignSystemGuardTests
{
    private static string WwwRoot(params string[] parts) =>
        Path.Combine(new[] { Root(), "src", "FullWorth.Web", "wwwroot" }.Concat(parts).ToArray());

    /// <summary>
    /// Es gibt kein zweites Dokument mehr. Ein eigenes index.html hieße eigene Stil-Kette, eigener
    /// Einstieg, eigene Kopfzeile — genau die drei Dinge, die auseinanderlaufen.
    /// </summary>
    [Fact]
    public void AdminIsAViewInTheOneDocument()
    {
        Assert.False(Directory.Exists(WwwRoot("admin")), "wwwroot/admin ist die alte Fremdseite und muss weg sein.");

        var html = File.ReadAllText(WwwRoot("index.html"));
        Assert.Contains("id=\"view-admin\"", html);
        Assert.Contains("/pages/admin/page.css", html);

        // Der Eintrag steht im Menü und wird nur eingeblendet, wenn die Sitzung Adminrechte hat.
        // Die Berechtigung selbst liegt am Server, nicht an diesem Attribut.
        var app = File.ReadAllText(WwwRoot("app.js"));
        Assert.Contains("[data-entry=\"admin\"]", app);
        Assert.Contains(".register('admin'", app);
    }

    /// <summary>
    /// Tokens statt Farben, geteilte Knopfrollen statt eigener, der geteilte Dialog statt eines
    /// eigenen &lt;dialog&gt;. Und nichts, was die Hülle schon stellt.
    /// </summary>
    [Fact]
    public void AdminStylesUseTokensAndAddOnlyWhatIsItsOwn()
    {
        // Kommentare sind Prosa über den alten Zustand, keine Regeln — sonst scheitert der Wächter an
        // seiner eigenen Erklärung.
        var css = Regex.Replace(
            File.ReadAllText(WwwRoot("pages", "admin", "page.css")), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var js = File.ReadAllText(WwwRoot("pages", "admin", "page.js"));
        var markup = File.ReadAllText(WwwRoot("pages", "admin", "page.html"));

        var hexColours = Regex.Matches(css, "#[0-9a-fA-F]{3,8}\\b").Select(match => match.Value).ToArray();
        Assert.True(hexColours.Length == 0,
            $"page.css muss Tokens benutzen, gefunden: {string.Join(", ", hexColours)}");
        Assert.DoesNotContain("prefers-color-scheme", css);

        // Was die Hülle stellt, darf die Seite nicht noch einmal bauen: keine eigene Hülle, keine
        // eigene Kopfzeile, kein eigener Melder.
        foreach (var own in new[] { ".admin-shell", ".admin-header", "#admin-toast" })
            Assert.DoesNotContain(own, css);

        foreach (var role in new[] { "btn btn-secondary", "btn btn-danger" }) Assert.Contains(role, js);
        Assert.Contains("createDialog(", js);
        Assert.DoesNotContain("<dialog", markup);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
