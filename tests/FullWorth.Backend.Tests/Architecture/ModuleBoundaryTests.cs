using System.Text.RegularExpressions;

namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Welche Module einander kennen — gemessen, nicht angenommen.
///
/// Neunundzwanzig Module, hundertachtundzwanzig Kanten, dreizehn Paare, die sich gegenseitig
/// importieren. Das Frontend verbietet inzwischen, dass eine Seite in eine andere greift, und prüft
/// es; im Backend gab es dafür nichts, und entsprechend ist der Graph gewachsen.
///
/// Dieser Test verlangt keine Null. Eine Null wäre heute rot und bliebe es über den ganzen Umbau,
/// und einen dauerhaft roten Test liest niemand — dieselbe Lehre wie beim Layout-Budget. Er hält
/// fest, was ist, und lässt es nur noch kleiner werden.
/// </summary>
public sealed class ModuleBoundaryTests
{
    /// <summary>
    /// Die dreizehn Paare, die sich am 2026-09-14 gegenseitig importieren. Fünf davon hängen an
    /// <c>Parity</c>, und das ist kein Zufall: dort liegen 151 der 648 Endpunkte, fachlich verteilt
    /// über mindestens acht andere Module. Ein Modul, das von allem ein Stück enthält, muss alles
    /// kennen — und wird von allem gekannt.
    ///
    /// Ein Eintrag darf verschwinden, keiner darf dazukommen.
    /// </summary>
    private static readonly string[] BekannteZyklen =
    [
        "Accounts <-> FullWorthSpaces",
        "Accounts <-> Users",
        "Audit <-> Parity",
        "Budgets <-> Parity",
        "Categories <-> Parity",
        "Categories <-> Transactions",
        "Coach <-> Intelligence",
        "Contracts <-> Intelligence",
        "Contracts <-> Parity",
        "FullWorthSpaces <-> Users",
        "Parity <-> Portfolio",
        "Purchases <-> Transactions",
        "Tax <-> Users"
    ];

    [Fact]
    public void No_new_pair_of_modules_imports_each_other()
    {
        var neu = Zyklen().Except(BekannteZyklen, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(neu.Length == 0,
            "Diese Module importieren sich neuerdings gegenseitig. Was zwei brauchen, gehoert in das "
            + "Modul, dem es fachlich gehoert - nicht in beide:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", neu));
    }

    /// <summary>
    /// Und die Ratsche: ein aufgelöster Zyklus verschwindet auch aus der Liste. Ohne das bliebe sie
    /// stehen, und niemand wüsste, ob sie noch die Wahrheit ist.
    /// </summary>
    [Fact]
    public void The_list_claims_no_cycle_that_is_gone()
    {
        var verschwunden = BekannteZyklen.Except(Zyklen(), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(verschwunden.Length == 0,
            "Diese Zyklen gibt es nicht mehr - schoen. Bitte aus der Liste in diesem Test streichen, "
            + "damit sie weiter gilt:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", verschwunden));
    }

    /// <summary>
    /// Gezählt werden echte <c>using</c>-Zeilen, nicht Erwähnungen. Ein Modulname in einem Kommentar
    /// ist keine Abhängigkeit, und ein Wächter, der Prosa liest, meldet irgendwann seinen eigenen Satz.
    /// </summary>
    private static IEnumerable<string> Zyklen()
    {
        var wurzel = Path.Combine(RepositoryRoot(), "src", "FullWorth.Backend", "Modules");
        var kanten = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var ordner in Directory.EnumerateDirectories(wurzel))
        {
            var modul = Path.GetFileName(ordner);
            var ziele = new HashSet<string>(StringComparer.Ordinal);
            foreach (var datei in Directory.EnumerateFiles(ordner, "*.cs", SearchOption.AllDirectories))
                foreach (Match treffer in Regex.Matches(
                    File.ReadAllText(datei), @"(?m)^\s*using\s+FullWorth\.Backend\.Modules\.([A-Za-z0-9_]+)\s*;"))
                    if (treffer.Groups[1].Value != modul) ziele.Add(treffer.Groups[1].Value);
            kanten[modul] = ziele;
        }

        return from paar in kanten
               from ziel in paar.Value
               where string.CompareOrdinal(paar.Key, ziel) < 0
                  && kanten.TryGetValue(ziel, out var zurueck) && zurueck.Contains(paar.Key)
               select $"{paar.Key} <-> {ziel}";
    }

    private static string RepositoryRoot()
    {
        var verzeichnis = new DirectoryInfo(AppContext.BaseDirectory);
        while (verzeichnis is not null && !File.Exists(Path.Combine(verzeichnis.FullName, "FullWorth.slnx")))
            verzeichnis = verzeichnis.Parent;

        return verzeichnis?.FullName ?? throw new DirectoryNotFoundException("FullWorth.slnx not found.");
    }
}
