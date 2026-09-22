using System.Text.RegularExpressions;
using FullWorth.Backend.Modules.Coach;

namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Die Gruende einer Ausgabenbewertung stehen zweimal da (#177).
///
/// Das Backend haelt sie in <see cref="SpendingReviewService"/> und PRUEFT dagegen - was nicht im
/// Katalog steht, wird beim Speichern stillschweigend verworfen, ohne Fehler. Das Frontend haelt
/// dieselben Schluessel noch einmal in <c>pages/coach/page.js</c>, weil dort auch die Beschriftungen
/// in zwei Sprachen liegen; die gehoeren dorthin und nicht in die Datenbank.
///
/// Es gab einen Endpunkt gegen genau dieses Problem - <c>GET /api/spending-reviews/reasons</c> - und
/// niemand hat ihn je aufgerufen. Ein Katalog, den man erst laden muss, verschiebt ausserdem die
/// Knoepfe nach dem ersten Zeichnen. Also bleiben es zwei Listen, und dieser Test ist der Grund,
/// warum das in Ordnung ist: sie duerfen nicht auseinanderlaufen.
///
/// Ohne ihn faellt es niemandem auf. Ein neuer Grund nur im Frontend erzeugt einen Knopf, dessen
/// Klick spurlos verschwindet; nur im Backend erzeugt einen Grund, den niemand sehen kann.
/// </summary>
public sealed class SpendingReviewReasonParityTests
{
    [Fact]
    public void The_frontend_offers_exactly_the_reasons_the_backend_accepts()
    {
        var frontend = FrontendReasons();
        foreach (var (sentiment, reasons) in SpendingReviewService.ReasonCatalog)
        {
            Assert.True(frontend.ContainsKey(sentiment.ToString()),
                $"Das Frontend kennt die Bewertung \"{sentiment}\" nicht.");
            Assert.Equal(
                reasons.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                frontend[sentiment.ToString()].OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }

        var known = SpendingReviewService.ReasonCatalog.Keys.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(frontend.Keys.Where(x => !known.Contains(x)));
    }

    [Fact]
    public void Every_offered_reason_has_a_label()
    {
        var page = Page();
        var labels = Block(page, "reasonLabels");
        foreach (var reason in FrontendReasons().SelectMany(x => x.Value))
            Assert.True(Regex.IsMatch(labels, @"(^|[\s{,])" + Regex.Escape(reason) + @"\s*:"),
                $"Der Grund \"{reason}\" hat keine Beschriftung - der Knopf zeigte seinen Schluessel.");
    }

    private static Dictionary<string, string[]> FrontendReasons()
    {
        var block = Block(Page(), "reasonsBySentiment");
        var groups = Regex.Matches(block, @"(\w+)\s*:\s*\[([^\]]*)\]");
        Assert.NotEmpty(groups);
        return groups.ToDictionary(
            match => match.Groups[1].Value,
            match => Regex.Matches(match.Groups[2].Value, @"'([^']+)'").Select(x => x.Groups[1].Value).ToArray(),
            StringComparer.Ordinal);
    }

    private static string Block(string source, string name)
    {
        var start = source.IndexOf($"const {name} = {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} steht nicht mehr in pages/coach/page.js.");
        var end = source.IndexOf("\n};", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{name} ist nicht mehr als Block zu lesen.");
        return source[start..end];
    }

    private static string Page()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "FullWorth.Web", "wwwroot", "pages", "coach", "page.js"));
    }
}
