using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Architecture;

/// <summary>
/// Waechter fuer die gemeinsame Assistenten-Komponente (Issue #157, Scheibe "components/wizard.js"):
/// sechs mehrschrittige Ablaeufe hatten vorher je ihren eigenen Schritt-Koerper, ihre eigene
/// "N / M"-Anzeige und ihre eigene Knopf-Sperre waehrend eines API-Aufrufs - letztere gelegentlich
/// ohne <c>finally</c>, sodass ein Knopf nach einem Fehler dauerhaft gesperrt blieb. Alle sechs
/// benutzen jetzt <c>createWizard()</c>/<c>withBusyButtons()</c> aus <c>components/wizard.js</c>.
///
/// Der Waechter ist bewusst eng: <c>data-step</c> ist das Merkmal, das ein von Hand gebautes
/// Schritt-Markup ausserhalb dieser Komponente ueberhaupt erst erkennbar macht (die Komponente selbst
/// braucht kein solches Attribut - sie haengt ihren Fortschritt als eigenes Element hinter den Kopf,
/// siehe <c>createWizard()</c>). Eine neue Datei mit <c>data-step</c>, die nicht aus
/// <c>components/wizard.js</c> importiert, baut ihre eigene Schrittzaehlung nach, statt die
/// gemeinsame zu verwenden.
/// </summary>
public sealed class WizardGuardTests
{
    private static string ListenPfad => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Architecture", "wizard-violations.txt");

    private static readonly Regex DataStep = new(@"\bdata-step\b", RegexOptions.Compiled);
    private static readonly Regex ImportsWizard = new(@"components/wizard\.js", RegexOptions.Compiled);

    [Fact]
    public void No_further_file_hand_rolls_step_markup_outside_the_wizard_component()
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
            "Diese Dateien bauen eigenes Schritt-Markup (data-step) statt createWizard()/"
            + "withBusyButtons() aus components/wizard.js zu verwenden:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", neu));
    }

    /// <summary>Ratsche: ein migrierter Ablauf verschwindet auch aus der Liste.</summary>
    [Fact]
    public void The_list_claims_no_file_that_is_clean_again()
    {
        var pfad = Path.GetFullPath(ListenPfad);
        if (!File.Exists(pfad)) return;

        var bekannt = File.ReadAllLines(pfad).Where(zeile => zeile.Trim().Length > 0);
        var sauber = bekannt.Except(Verstoesse(), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(sauber.Length == 0,
            "Diese Dateien sind migriert - bitte aus tests/.../Architecture/wizard-violations.txt "
            + "streichen:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", sauber));
    }

    private static string[] Verstoesse()
    {
        var wwwroot = WwwRoot();
        var offenders = new List<string>();

        foreach (var datei in Directory.EnumerateFiles(wwwroot, "*.js", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(wwwroot, datei).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == "components/wizard.js") continue;
            if (relative.Contains("/lib/", StringComparison.Ordinal)) continue;

            var source = File.ReadAllText(datei);
            if (DataStep.IsMatch(source) && !ImportsWizard.IsMatch(source))
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
