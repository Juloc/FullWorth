using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Architecture;

/// <summary>
/// Waechter fuer die gemeinsame Mehrfachauswahl (Issue #157, Scheibe "components/selection-list.js"):
/// sieben Stellen hatten vorher je ihre eigene Checkbox-Zeile, ihr eigenes "Alle auswaehlen" (mal mit,
/// mal ohne <c>indeterminate</c>) und ihren eigenen "{n}/{total}"-Zaehler neu erfunden - an zwei Stellen
/// fehlten "Alle auswaehlen" und der Zaehler sogar ganz. Alle sieben (plus, seit dieser Scheibe, die
/// achte in <c>pages/settings/import/page.js</c>) benutzen jetzt <c>createSelectionList()</c>.
///
/// Ein einzelnes <c>type="checkbox"</c> ist KEIN Signal - das ist eine der haeufigsten Formen der App
/// (ein einzelnes Schalter-Feld, eine Zustimmung, ein Formularfeld; 23 von 26 Dateien mit einer Checkbox
/// im Baum sind genau das und sollen hier nie anschlagen). Erst die KOMBINATION aus einer Checkbox UND
/// einer <c>.indeterminate =</c>-Zuweisung ist eng genug: <c>indeterminate</c> ist ein Zustand, den nur
/// ein "Alle auswaehlen"-Kopfkaestchen braucht, wenn ein Teil (nicht alles, nicht nichts) einer Menge
/// ausgewaehlt ist - also genau der Mehrfachauswahl-Fall, den <c>createSelectionList()</c> abdeckt, und
/// sonst nichts in dieser App.
/// </summary>
public sealed class SelectionListGuardTests
{
    private static string ListenPfad => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Architecture", "selection-list-violations.txt");

    private static readonly Regex Checkbox = new(@"type\s*=\s*[""']checkbox[""']", RegexOptions.Compiled);
    private static readonly Regex IndeterminateAssignment = new(@"\.indeterminate\s*=", RegexOptions.Compiled);
    private static readonly Regex ImportsSelectionList = new(@"components/selection-list\.js", RegexOptions.Compiled);

    [Fact]
    public void No_further_file_hand_rolls_a_checkbox_row_multi_select_outside_the_shared_component()
    {
        var aktuell = Verstoesse();
        var pfad = Path.GetFullPath(ListenPfad);

        if (!File.Exists(pfad))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            File.WriteAllLines(pfad, aktuell);
            Assert.Fail($"Die Liste wurde neu geschrieben ({aktuell.Length} Zeilen): {pfad}");
        }

        var bekannt = File.ReadAllLines(pfad).Where(zeile => zeile.Trim().Length > 0).ToArray();
        var neu = aktuell.Except(bekannt, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(neu.Length == 0,
            "Diese Dateien bauen eine Checkbox-Zeilen-Mehrfachauswahl (Checkbox + indeterminate) von "
            + "Hand statt createSelectionList()/selectionListHtml() aus components/selection-list.js zu "
            + "verwenden:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", neu));
    }

    /// <summary>Ratsche: eine migrierte Stelle verschwindet auch aus der Liste.</summary>
    [Fact]
    public void The_list_claims_no_file_that_is_clean_again()
    {
        var pfad = Path.GetFullPath(ListenPfad);
        if (!File.Exists(pfad)) return;

        var bekannt = File.ReadAllLines(pfad).Where(zeile => zeile.Trim().Length > 0);
        var sauber = bekannt.Except(Verstoesse(), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(sauber.Length == 0,
            "Diese Dateien sind migriert - bitte aus tests/.../Architecture/"
            + "selection-list-violations.txt streichen:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", sauber));
    }

    private static string[] Verstoesse()
    {
        var wwwroot = WwwRoot();
        var offenders = new List<string>();

        foreach (var datei in Directory.EnumerateFiles(wwwroot, "*.js", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(wwwroot, datei).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == "components/selection-list.js") continue;
            if (relative.Contains("/lib/", StringComparison.Ordinal)) continue;

            var source = File.ReadAllText(datei);
            if (Checkbox.IsMatch(source) && IndeterminateAssignment.IsMatch(source) && !ImportsSelectionList.IsMatch(source))
                offenders.Add(relative);
        }

        return offenders.Order(StringComparer.Ordinal).ToArray();
    }

    private static string WwwRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "FullWorth.Web", "wwwroot");
    }
}
