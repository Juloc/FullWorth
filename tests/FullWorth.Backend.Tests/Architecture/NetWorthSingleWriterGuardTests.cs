using System.Text.RegularExpressions;

namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Die Vermoegenshistorie hat genau EINEN Schreiber (#123).
///
/// <c>NetWorthSnapshotService.RebuildHistoryFor*</c> liest die vorhandenen Snapshots, ergaenzt die
/// fehlenden und speichert. Zwei Laeufe nebeneinander sehen beide "heute fehlt" und legen beide an:
///
/// <code>
/// ERROR: duplicate key value violates unique constraint
///        "IX_NetWorthSnapshots_FullWorthSpaceId_UserId_Date_Currency"
/// </code>
///
/// Der <c>FinancialDataConsistencyCoordinator</c> ist ein Singleton mit einem Semaphor und
/// serialisiert seine Laeufe. Er wird vom SaveChanges-Interceptor nach JEDEM vermoegenswirksamen
/// Commit angestossen - jeder weitere Aufrufer laeuft an diesem Semaphor vorbei und ist zugleich
/// ueberfluessig.
///
/// <para>
/// Warum ein Quelltext-Waechter und kein Ablauftest: der Fehler braucht echte Nebenlaeufigkeit. Ein
/// Test, der den Endpunkt einmal aufruft, laeuft sequenziell - der zweite Neuaufbau findet die schon
/// committete Zeile und legt nichts an. Genau deshalb blieb ein solcher Test gruen, waehrend der
/// zweite Schreiber wieder im Endpunkt stand. Die Zusicherung ist strukturell ("genau ein Aufrufer"),
/// also wird sie strukturell geprueft.
/// </para>
/// </summary>
public sealed class NetWorthSingleWriterGuardTests
{
    /// <summary>
    /// Die einzige Datei, die den Neuaufbau anstossen darf. Wer hier eine zweite eintraegt, sollte
    /// vorher erklaeren koennen, wie sie sich mit dem Semaphor des Koordinators vertraegt.
    /// </summary>
    private const string ErlaubterAufrufer = "Data/FinancialDataConsistency.cs";

    [Fact]
    public void OnlyTheConsistencyCoordinatorRebuildsTheHistory()
    {
        var aufrufer = Aufrufer();

        Assert.True(
            aufrufer.Length == 1 && aufrufer[0] == ErlaubterAufrufer,
            "Die Vermoegenshistorie darf nur ueber den FinancialDataConsistencyCoordinator neu "
            + "aufgebaut werden - er serialisiert seine Laeufe. Diese Dateien stossen sie ausserdem an:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", aufrufer));
    }

    private static string[] Aufrufer()
    {
        var wurzel = Path.Combine(RepositoryRoot(), "src", "FullWorth.Backend");

        return Directory.EnumerateFiles(wurzel, "*.cs", SearchOption.AllDirectories)
            .Where(datei => !datei.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !datei.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                // Der Dienst selbst darf sich naturgemaess nennen.
                && !datei.EndsWith("NetWorthSnapshotService.cs", StringComparison.Ordinal))
            .Where(datei => Regex.IsMatch(
                OhneKommentare(File.ReadAllText(datei)),
                @"\.(RebuildHistoryForUserAsync|RebuildHistoryForSpaceAsync|RebuildAllHistoryAsync|CaptureForUserAsync|CaptureTodayAsync)\s*\("))
            .Select(datei => Path.GetRelativePath(wurzel, datei).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

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
