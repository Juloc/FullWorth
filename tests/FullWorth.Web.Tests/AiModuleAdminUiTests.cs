namespace FullWorth.Web.Tests;

/// <summary>
/// Die Freigabe je Modul in der Admin-Oberflaeche.
///
/// Vorher standen dort sieben feste Kaestchen, und im Backend sieben Spalten dazu. Eine neue Funktion
/// kostete beides. Jetzt zeichnet die Seite, was der Server als <c>availableModules</c> schickt - und
/// genau das ist hier gepinnt: nicht das Aussehen, sondern dass die Liste nicht wieder in diese Datei
/// wandert.
/// </summary>
public sealed class AiModuleAdminUiTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(
        Root(), "src", "FullWorth.Web", "wwwroot", "pages", "settings", "intelligence", name));

    /// <summary>Die sieben Kaestchen sind weg - aus dem Markup UND aus dem Skript.</summary>
    [Fact]
    public void The_seven_hardcoded_module_checkboxes_are_gone()
    {
        var html = Read("page.html");
        var js = Read("page.js");

        foreach (var id in new[] { "ai-receipt", "ai-merchant", "ai-category", "ai-contract", "ai-product", "ai-logo", "ai-internet" })
        {
            Assert.DoesNotContain(id, html, StringComparison.Ordinal);
            Assert.DoesNotContain(id, js, StringComparison.Ordinal);
        }

        // Und die Felder, die es im Backend nicht mehr gibt, werden auch nicht mehr geschickt.
        foreach (var field in new[] { "receiptAiEnabled", "merchantAiEnabled", "categoryAiEnabled", "logoResearchEnabled" })
            Assert.DoesNotContain(field, js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der eigentliche Punkt: WELCHE Module es gibt, sagt der Server. Stuende die Liste hier, waere
    /// nichts gewonnen - dann haette ein neues Modul nur den Ort gewechselt, an dem es gepflegt wird.
    /// </summary>
    [Fact]
    public void The_page_draws_the_list_the_server_sends()
    {
        var js = Read("page.js");

        Assert.Contains("settings.availableModules", js);
        Assert.Contains("settings.modules", js);
        Assert.Contains("modules: selectedModules()", js);
        // Ein leerer Behaelter im Dokument, gefuellt beim Zeichnen - kein Nachladen (Frontend-Regel 2).
        Assert.Contains("id=\"ai-modules\"", Read("page.html"));
    }

    /// <summary>
    /// Die deutschen Namen duerfen hier stehen, wie jeder andere Text dieser Seite. Was nicht sein
    /// darf: dass ein Modul ohne Eintrag aus der Liste verschwindet - dann waere es unsichtbar
    /// freigebbar. Es zeigt dann seinen Schluessel.
    /// </summary>
    [Fact]
    public void A_module_without_a_german_label_still_appears()
    {
        Assert.Contains("MODULE_LABELS[module] || module", Read("page.js"));
    }
}
