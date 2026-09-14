using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Infrastructure;

/// <summary>
/// Wer eine Testdatenbank anlegt, räumt auch wieder auf.
///
/// Die Backend-Factory tat das seit Langem, die Web-Factory nicht — und niemandem fiel es auf, weil
/// beide auf denselben Server zeigen: solange irgendwann Backend-Tests liefen, räumten die auch die
/// Web-Datenbanken nicht weg (sie filtern auf ihr eigenes Präfix), aber der Server wuchs langsam
/// genug, um nicht zu stören.
///
/// Am 2026-09-14 waren es 5 846 Klone mit 48 GB, und das Ende war nicht die Platte: PostgreSQL lief
/// mit Dockers voreingestellten 64 MB <c>/dev/shm</c> in
/// "could not resize shared memory segment … No space left on device", während C: 50 GB frei hatte.
/// Eine Fehlermeldung, die auf das Falsche zeigt, kostet Stunden.
///
/// Dieser Test ist billig und stumpf: er liest die Factories und besteht darauf, dass jede, die eine
/// Datenbank anlegt, auch eine aufräumt. Eine dritte Factory ohne Aufräumen fällt damit beim Anlegen
/// auf, nicht in drei Wochen.
/// </summary>
public sealed class TestDatabaseHygieneTests
{
    private static readonly string TestsRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Every_factory_that_creates_a_database_also_drops_old_ones()
    {
        var factories = Directory
            .EnumerateFiles(Path.Combine(TestsRoot, "tests"), "*Factory*.cs", SearchOption.AllDirectories)
            .Where(path => Regex.IsMatch(File.ReadAllText(path), @"Database=fullworth_\w+_\{"))
            .ToArray();

        Assert.NotEmpty(factories);

        var offenders = factories
            .Where(path => !File.ReadAllText(path).Contains("PurgeAbandonedDatabases", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(TestsRoot, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Diese Factories legen Datenbanken an und räumen keine weg:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
