using System.Text.RegularExpressions;

namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Eine Middleware laesst durch oder weist ab. Antworten tut der Handler (#177).
///
/// Zwei Middlewares taten es trotzdem. <c>BudgetReconciliationCompatibilityMiddleware</c> beantwortete
/// die drei Budget-Status-Routen, <c>FinancialReconciliationMiddleware</c> die freie Auswertung, das
/// Flussdiagramm und den verfuegbaren Betrag - jeweils VOR der Zuordnung. Die gemappten Handler liefen
/// nie, standen aber weiter da: gruen getestet, in der Routenflaeche, mit Aufrufern im Frontend. Fuenf
/// Fassungen des Budgetstands sind so entstanden, vier davon unerreichbar, und eine davon haette bei
/// einem Budget auf einer Oberkategorie 100 statt 20 gemeldet, sobald der Schatten ueber ihr wegfiel.
///
/// Der Fehler ist nicht zu sehen: die Route existiert, der Aufrufer existiert, die Tests sind gruen,
/// und die beiden Fassungen begegnen sich nie. Deshalb hier eine Regel, die strukturell verhindert,
/// dass es wieder eine gibt - eine Middleware, die keinen Rumpf schreiben kann, kann keinen Handler
/// ueberholen.
/// </summary>
public sealed class MiddlewareDoesNotAnswerTests
{
    /// <summary>Womit man einen Antwortrumpf schreibt. Ein Statuscode allein ist ein Abweisen.</summary>
    private static readonly string[] WritesABody =
        ["WriteAsJsonAsync", "WriteAsync(", "Results.Ok", "Results.Json", "Results.Content", "Results.File"];

    [Fact]
    public void A_middleware_refuses_or_passes_through_but_never_answers()
    {
        var offenders = Middlewares()
            .SelectMany(file => WritesABody
                .Where(marker => file.Text.Contains(marker, StringComparison.Ordinal))
                .Select(marker => $"{Path.GetFileName(file.Path)}: {marker}"))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Diese Middlewares schreiben einen Antwortrumpf. Damit ueberholen sie den gemappten " +
            "Handler, und von da an gibt es zwei Fassungen derselben Antwort, von denen nur eine " +
            "ankommt. Was geantwortet wird, gehoert in den Handler:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    [Fact]
    public void Every_middleware_can_let_a_request_through()
    {
        var stuck = Middlewares()
            .Where(file => !file.Text.Contains("next(context)", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file.Path))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(stuck.Length == 0,
            "Diese Middlewares reichen keine Anfrage weiter - sie sind keine Middleware, sondern ein " +
            "Handler an der falschen Stelle:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", stuck));
    }

    private static IEnumerable<(string Path, string Text)> Middlewares()
    {
        var root = Path.Combine(Root(), "src", "FullWorth.Backend");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(file => Regex.IsMatch(file.Text, @"class\s+\w*Middleware\b"))
            .ToArray();

        // Ohne diese Zusicherung waere der Test lautlos leer, sobald jemand die Dateien umbenennt.
        Assert.NotEmpty(files);
        return files;
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
