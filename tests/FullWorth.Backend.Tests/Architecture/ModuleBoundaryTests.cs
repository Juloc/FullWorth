using System.Text.RegularExpressions;

namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Welche Module einander kennen — gemessen, nicht angenommen.
///
/// Am Anfang des Umbaus: 29 Module, 128 Kanten, 13 Paare, die sich gegenseitig importieren. Das
/// Frontend verbietet inzwischen, dass eine Seite in eine andere greift, und prüft es; im Backend gab
/// es dafür nichts, und entsprechend ist der Graph gewachsen.
///
/// Dieser Test verlangt keine Null. Eine Null wäre am ersten Tag rot gewesen und über den ganzen
/// Umbau rot geblieben, und einen dauerhaft roten Test liest niemand — dieselbe Lehre wie beim
/// Layout-Budget. Er hält fest, was ist, und lässt es nur noch kleiner werden.
/// </summary>
public sealed class ModuleBoundaryTests
{
    /// <summary>
    /// Was am 2026-09-14 noch übrig ist: vier von ursprünglich dreizehn.
    ///
    /// Neun sind an diesem Tag gefallen, und keiner davon durch Verhandeln — jedes Mal lag ein Stück
    /// Code in einem Modul, dem es nicht gehörte:
    ///
    /// <list type="bullet">
    /// <item><b>Vier auf einen Schnitt</b> (Audit, Budgets, Categories, Contracts ↔ Parity):
    ///   <c>HasCapabilityAsync</c> stand in einer Klasse namens PermissionsErgonomicsParityEndpoints,
    ///   also in einer Endpunktdatei, obwohl es eine reine Berechtigungsprüfung ist. Sie liegt jetzt
    ///   als <c>Security/SpaceCapabilities</c>.</item>
    /// <item><b>Drei auf einen Umzug</b> (Accounts, FullWorthSpaces, Tax ↔ Users): JEDE ausgehende
    ///   Kante von Users kam aus zwei Dateien — AccountPurgeService und PersonalDataPurgeManifest. Das
    ///   Löschen eines Kontos muss wissen, wo überall persönliche Daten liegen, und machte Users damit
    ///   zum Modul, das alles importiert. Es ist ein eigener Belang: <c>Modules/DataErasure</c>.</item>
    /// <item><b>Categories ↔ Transactions:</b> CategoryIntelligenceModule lag in Transactions, hieß
    ///   nach Kategorien und mappte /api/category-intelligence.</item>
    /// <item><b>Parity ↔ Portfolio:</b> InvestmentNetWorthService lag in Parity, wurde aber schon immer
    ///   neben NetWorthSnapshotService registriert.</item>
    /// </list>
    ///
    /// Die vier übrigen sind anderer Natur — bei ihnen teilen sich zwei Module eine Modellfamilie, und
    /// sie zu trennen hieße, sie zu verdoppeln oder zu verschieben. Siehe #111.
    ///
    /// Ein Eintrag darf verschwinden, keiner darf dazukommen.
    /// </summary>
    private static readonly string[] BekannteZyklen =
    [
        "Accounts <-> FullWorthSpaces",
        "Coach <-> Intelligence",
        "Contracts <-> Intelligence",
        "Purchases <-> Transactions",
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
