using FullWorth.Backend.Validation;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Eine Zahl, die von außen kommt, wird von genau einem Leser gelesen.
///
/// Die Regel steht in CLAUDE.md, und sie steht dort, weil sie schon einmal gebrochen war: jeder
/// Importer brachte seinen eigenen Parser mit, und zwei davon lasen „1234.56" als 123456. Das ist als
/// P0-1 behoben worden — aber nichts hielt die Regel danach fest, und drei Importer hatten sich
/// inzwischen wieder ihren eigenen gebaut:
///
/// <list type="bullet">
/// <item>die Gehaltsabrechnung las „3.500" als dreieinhalb statt dreitausendfünfhundert,</item>
/// <item>die Depotabrechnung benutzte EINEN Leser für Kurs und Kurswert — bei „1.234" gehen die
///       richtigen Antworten dort um den Faktor tausend auseinander,</item>
/// <item>der Amazon-Leser ließ den Punkt stehen, wenn kein Komma im Text stand.</item>
/// </list>
///
/// Der Punkt ist nicht, dass <c>decimal.TryParse</c> verboten wäre. Der Punkt ist, dass die Frage
/// „ist die dreistellige Endgruppe eine Tausendergruppe oder sind es Nachkommastellen" je Feld
/// beantwortet werden muss, und dass <see cref="ImportNumber.ThreeDigitTail"/> genau dafür da ist.
/// Ein selbstgebauter Leser beantwortet sie einmal für alles und liegt damit bei der Hälfte falsch.
///
/// Was unten steht, ist deshalb kein Verbot, sondern eine Liste: diese vier Stellen lesen keine
/// importierte Zahl, und jede sagt warum. Eine fünfte macht den Test rot.
/// </summary>
public sealed class ImportNumberIsTheOnlyParserTests
{
    /// <summary>
    /// Stellen, die selbst zerlegen dürfen, mit dem Grund. Der Grund ist der eigentliche Inhalt: wer
    /// hier etwas einträgt, muss sagen können, warum die Zahl nicht importiert ist.
    /// </summary>
    private static readonly Dictionary<string, string> Erlaubt = new(StringComparer.Ordinal)
    {
        // Der Leser selbst steht nicht hier: er liegt seit dem Umzug in Validation/ und damit
        // ausserhalb von Modules. Das war kein Aufraeumen, sondern der einzige Ausweg - er wird von
        // fuenf Modulen benutzt, gehoert also in keines, und solange er in Parity lag, holte sich
        // jeder Benutzer eine Kante nach Parity. Bei Purchases war das prompt ein Zyklus.
        ["Coach/DeterministicCoachEngine.cs"] =
            "Zahlen aus dem Text des Coaches, also aus unserem eigenen Haus - kein Bankauszug, keine Texterkennung.",
        ["Intelligence/AiCostEstimator.cs"] =
            "Ein Preis aus der Konfiguration, in fester Schreibweise hinterlegt.",
        ["Purchases/PurchaseCaptureService.cs"] =
            "Ein Feld aus unserem eigenen Formular; das Frontend schickt die Zahl bereits in kanonischer Form."
    };

    [Fact]
    public void Only_the_shared_reader_parses_an_imported_number()
    {
        var modules = Path.Combine(RepositoryRoot(), "src", "FullWorth.Backend", "Modules");

        var offenders = Directory.EnumerateFiles(modules, "*.cs", SearchOption.AllDirectories)
            .Where(file => Regex.IsMatch(OhneKommentare(File.ReadAllText(file)), @"\bdecimal\.(Try)?Parse\s*\("))
            .Select(file => Path.GetRelativePath(modules, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(name => !Erlaubt.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Diese Dateien zerlegen eine Zahl selbst, statt ImportNumber zu benutzen. Wenn die Zahl "
            + "wirklich nicht importiert ist, gehoert sie mit Begruendung in die Liste in diesem Test:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>
    /// Und das Gegenstück: die Liste darf nicht verrotten. Ein Eintrag, den es nicht mehr gibt, ist
    /// eine Erlaubnis, die niemand mehr prüft.
    /// </summary>
    [Fact]
    public void The_list_names_no_file_that_is_gone()
    {
        var modules = Path.Combine(RepositoryRoot(), "src", "FullWorth.Backend", "Modules");

        var verschwunden = Erlaubt.Keys
            .Where(name => !File.Exists(Path.Combine(modules, name.Replace('/', Path.DirectorySeparatorChar))))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(verschwunden.Length == 0,
            "Diese Ausnahmen zeigen ins Leere:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", verschwunden));
    }

    /// <summary>
    /// Ein Wächter, der Prosa liest, meldet den Satz, mit dem jemand erklärt hat, warum es ihn gibt.
    /// In diesem Projekt ist genau das schon passiert.
    /// </summary>
    private static string OhneKommentare(string code) =>
        Regex.Replace(
            Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"(?m)^\s*//.*$", string.Empty);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("FullWorth.slnx not found.");
    }
}
