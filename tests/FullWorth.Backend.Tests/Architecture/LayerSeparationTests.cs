using System.Text.RegularExpressions;

namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Eine Schicht je Datei: <c>*Endpoints.cs</c> übersetzt HTTP, <c>*Store.cs</c> besitzt die Daten.
///
/// Gemessen am 2026-09-14: von 112 Dateien, die Routen mappen, greifen 96 aus dem Handler heraus
/// selbst in die Datenbank. Gleichzeitig gibt es 30 <c>*Store.cs</c> — die Bauform ist da, sie wird
/// nur nicht durchgehalten. <c>Pension</c> zeigt, wie es aussieht, wenn man es tut: PensionEndpoints
/// mappt, PensionStore besitzt, und die Reihenfolge nicht-gefunden → verboten → Konflikt steht an
/// einer Stelle statt in jedem Handler neu.
///
/// Der Punkt ist nicht Schönheit. Solange ein Handler selbst abfragt, wird die Abfrage beim nächsten
/// Handler kopiert statt benannt — und eine kopierte Abfrage wird irgendwann nur an einer der Stellen
/// korrigiert.
///
/// Das ist eine Ratsche, keine Forderung nach null: die 96 sind aufgeschrieben, ein 97. macht rot.
/// Wer eine Datei aufteilt, streicht sie aus der Liste — und der zweite Test besteht darauf.
/// Siehe #113.
/// </summary>
public sealed class LayerSeparationTests
{
    private static string ListenPfad => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Architecture", "layer-violations.txt");

    [Fact]
    public void No_further_file_mixes_http_and_the_database()
    {
        var aktuell = Vermischte();
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
            "Diese Dateien mappen Routen UND fassen die Datenbank an. Die Abfrage gehoert in einen "
            + "Store, der ihr einen Namen gibt:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", neu));
    }

    /// <summary>
    /// Und die Ratsche: eine aufgeteilte Datei verschwindet auch aus der Liste. Sonst stünde sie da
    /// und niemand wüsste, ob sie noch die Wahrheit ist.
    /// </summary>
    [Fact]
    public void The_list_claims_no_file_that_is_clean_again()
    {
        var pfad = Path.GetFullPath(ListenPfad);
        if (!File.Exists(pfad)) return;

        var bekannt = File.ReadAllLines(pfad).Where(zeile => zeile.Trim().Length > 0);
        var sauber = bekannt.Except(Vermischte(), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(sauber.Length == 0,
            "Diese Dateien sind aufgeteilt - schoen. Bitte aus tests/.../Architecture/"
            + "layer-violations.txt streichen, damit die Liste weiter gilt:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", sauber));
    }

    private static string[] Vermischte()
    {
        var wurzel = Path.Combine(RepositoryRoot(), "src", "FullWorth.Backend", "Modules");

        return Directory.EnumerateFiles(wurzel, "*.cs", SearchOption.AllDirectories)
            .Where(datei =>
            {
                var code = OhneKommentare(File.ReadAllText(datei));
                return Regex.IsMatch(code, @"\bMap(Group|Get|Post|Put|Patch|Delete)\s*\(")
                    && Regex.IsMatch(code,
                        @"\bdb\.[A-Z][A-Za-z]+\.(Where|Any|FirstOrDefault|SingleOrDefault|Add|Remove|ToListAsync|AnyAsync)|FullWorthDbContext");
            })
            .Select(datei => Path.GetRelativePath(wurzel, datei).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Ein Wächter, der Prosa liest, meldet irgendwann den Satz, der ihn erklärt.</summary>
    private static string OhneKommentare(string code) =>
        Regex.Replace(
            Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"(?m)^\s*//.*$", string.Empty);

    private static string RepositoryRoot()
    {
        var verzeichnis = new DirectoryInfo(AppContext.BaseDirectory);
        while (verzeichnis is not null && !File.Exists(Path.Combine(verzeichnis.FullName, "FullWorth.slnx")))
            verzeichnis = verzeichnis.Parent;

        return verzeichnis?.FullName ?? throw new DirectoryNotFoundException("FullWorth.slnx not found.");
    }
}
