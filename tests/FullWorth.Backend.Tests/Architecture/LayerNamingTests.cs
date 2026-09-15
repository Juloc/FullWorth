namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Der Name sagt die Schicht: <c>*Endpoints.cs</c>, <c>*Store.cs</c>, <c>*Service.cs</c>.
///
/// „Module" sagt nichts. Es war der Name für eine Datei, die Routen, Fachlichkeit und Datenbank in
/// einem hatte - und genau weil er nichts aussagt, ist er nie jemandem im Weg gewesen. Die
/// Schichtentrennung selbst hält <see cref="LayerSeparationTests"/> fest; dieser Wächter hält den
/// Namen fest, damit die getrennte Bauform auch ablesbar bleibt.
///
/// Das war eine Ratsche und ist jetzt eine Regel: die Liste begann mit 26 Dateien und ist leer.
/// Eine neue <c>*Module.cs</c> macht rot, ohne Verhandlung. Siehe #113 Regel 6 und #134.
///
/// Was in den 26 steckte, war dreimal dasselbe Muster: eine EF-Entität, ein Dutzend DTOs und ein
/// Store in einer Datei. Sie liegen jetzt als <c>*Models.cs</c>, <c>*Dtos.cs</c> und <c>*Store.cs</c>
/// nebeneinander - dieselbe Aufteilung, die Pension schon hatte.
///
/// „Parity" und „V2" sind mit #133 aus jedem Routen- und Typnamen verschwunden.
/// </summary>
public sealed class LayerNamingTests
{
    private static string ListenPfad => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Architecture", "module-named-files.txt");

    [Fact]
    public void No_new_file_is_called_module()
    {
        var aktuell = ModuleDateien();
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
            "Diese Dateien heissen *Module.cs. Der Name soll die Schicht sagen - Endpoints, Store "
            + "oder Service:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", neu));
    }

    /// <summary>Und die Ratsche: eine umbenannte Datei verschwindet auch aus der Liste.</summary>
    [Fact]
    public void The_list_claims_no_file_that_is_gone()
    {
        var pfad = Path.GetFullPath(ListenPfad);
        if (!File.Exists(pfad)) return;

        var bekannt = File.ReadAllLines(pfad).Where(zeile => zeile.Trim().Length > 0);
        var weg = bekannt.Except(ModuleDateien(), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(weg.Length == 0,
            "Diese Dateien heissen nicht mehr *Module.cs - schoen. Bitte aus tests/.../Architecture/"
            + "module-named-files.txt streichen, damit die Liste weiter gilt:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", weg));
    }

    private static string[] ModuleDateien()
    {
        var wurzel = Path.Combine(RepositoryRoot(), "src", "FullWorth.Backend", "Modules");

        return Directory.EnumerateFiles(wurzel, "*Module.cs", SearchOption.AllDirectories)
            .Select(datei => Path.GetRelativePath(wurzel, datei).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string RepositoryRoot()
    {
        var verzeichnis = new DirectoryInfo(AppContext.BaseDirectory);
        while (verzeichnis is not null && !File.Exists(Path.Combine(verzeichnis.FullName, "FullWorth.slnx")))
            verzeichnis = verzeichnis.Parent;

        return verzeichnis?.FullName ?? throw new DirectoryNotFoundException("FullWorth.slnx not found.");
    }
}
