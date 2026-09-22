using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// Jedes KI-Modul hat einen Namen, den jemand lesen kann.
///
/// Die Module stehen als Daten im Katalog (<c>AiModules</c>), und die Freigabeseite zeigt sie. Ohne
/// Beschriftung faellt sie auf den Schluessel zurueck - dann steht dort <c>collection-suggestions</c>,
/// und genau das war der Fall, bis dieser Test geschrieben wurde. Es sieht nicht kaputt aus, es sieht
/// nur unfertig aus, und deshalb faellt es niemandem auf.
///
/// Der Test liest beide Seiten: die Konstanten im Katalog und die Tabelle in der Oberflaeche.
/// </summary>
public sealed class AiModuleLabelTests
{
    [Fact]
    public void Every_module_in_the_catalog_has_a_label()
    {
        var keys = ModuleKeys();
        var labels = LabelKeys();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
            Assert.Contains(key, labels);
    }

    /// <summary>
    /// Und umgekehrt: eine Beschriftung ohne Modul ist eine, die jemand vergessen hat wegzunehmen. Sie
    /// zeigt nichts an, kostet aber die naechste Person die Frage, wo dieses Modul geblieben ist.
    /// </summary>
    [Fact]
    public void Every_label_belongs_to_a_module()
    {
        var keys = ModuleKeys();

        foreach (var label in LabelKeys())
            Assert.Contains(label, keys);
    }

    private static HashSet<string> ModuleKeys()
    {
        var source = File.ReadAllText(Path.Combine(
            Root(), "src", "FullWorth.Backend", "Modules", "Intelligence", "AiModules.cs"));
        return Regex.Matches(source, @"public const string \w+ = ""([a-z-]+)"";")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> LabelKeys()
    {
        var source = File.ReadAllText(Path.Combine(
            Root(), "src", "FullWorth.Web", "wwwroot", "pages", "settings", "intelligence", "page.js"));
        var table = Regex.Match(source, @"const MODULE_LABELS = \{(.*?)\};", RegexOptions.Singleline);
        Assert.True(table.Success, "MODULE_LABELS wurde nicht gefunden.");
        return Regex.Matches(table.Groups[1].Value, @"'([a-z-]+)'\s*:")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
