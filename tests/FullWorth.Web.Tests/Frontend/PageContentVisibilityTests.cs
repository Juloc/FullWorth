using System.Text.Json;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// Jede Seite zeigt auch wirklich etwas.
///
/// Das klingt nach einer Selbstverständlichkeit und ist die Lücke, durch die in #154 zwei Seiten
/// gefallen sind. <c>pages/pension/page.css</c> und <c>pages/tax/page.css</c> trugen beide dasselbe
/// Paar aus der alten Hülle:
///
///     .pension-view      { display: none;  }
///     .pension-view.active { display: block; }
///
/// Solange die Hülle die Klasse <c>active</c> umschaltete, war das richtig. Seit jede Seite ihr
/// eigenes Dokument ist, gibt es <c>active</c> nicht mehr — übrig blieb die erste Hälfte, und beide
/// Seiten waren vollständig unsichtbar. Im Browser nachgemessen: <c>display: none</c>, Höhe 0.
///
/// Aufgefallen ist es nicht, und das ist der eigentliche Punkt. <see cref="LayoutStabilityTests"/>
/// besucht genau diese Adressen — aber eine leere Seite springt nicht, also war sie grün. Ein
/// Wächter, der nur "wird es schlechter" fragt, sagt nichts über eine Seite, die gar nichts mehr
/// zeigt; das ist dieselbe leere Zustimmung, die schon einmal auftrat, als die Harness bei einem
/// Fehler still auf die alte Hülle zurückfiel und alle 24 Messungen an der falschen Anwendung
/// vorbeiliefen.
///
/// Deshalb misst dieser hier die andere Richtung: der Rumpf, den die Razor-Seite unter die Topbar
/// setzt, muss sichtbar sein und Höhe haben. Zwei Bedingungen, beide billig, und zusammen fangen sie
/// den ganzen Fehlerkreis — ein hängengebliebenes <c>display:none</c>, ein Modul, das beim Start
/// wirft, bevor es zeichnet, ein leeres <c>@RenderBody()</c>.
/// </summary>
[Trait("Needs", "Browser")]
[Collection(nameof(UiHarnessCollection))]
public sealed class PageContentVisibilityTests(UiHarness harness)
{
    /// <summary>
    /// Die Untergrenze ist bewusst niedrig. Sie soll "die Seite ist nicht da" von "die Seite ist da"
    /// trennen, nicht beurteilen, wie viel eine Seite zu zeigen hat: die kleinste gemessene
    /// Rumpfhöhe liegt bei mehreren hundert Pixeln, und die beiden kaputten Seiten lagen bei 0.
    /// </summary>
    private const int MinimumHeight = 120;

    public static TheoryData<string> Pages()
    {
        var data = new TheoryData<string>();
        foreach (var path in LayoutStabilityTests.Paths) data.Add(path);
        return data;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task A_page_actually_shows_its_body(string path)
    {
        var answer = await harness.AskAsync(path, mobile: false, Probe);
        var body = JsonSerializer.Deserialize<BodyReport>(answer, JsonSerializerOptions.Web)!;

        Assert.True(body.Sections > 0, $"{path}: unter der Topbar steht nichts — RenderBody() lieferte nichts.");
        Assert.True(
            body.Hidden.Length == 0,
            $"{path}: der Rumpf ist versteckt ({string.Join(", ", body.Hidden)}). "
            + "Meistens eine display:none-Regel, deren Gegenstück (.active) mit der alten Hülle verschwunden ist.");
        Assert.True(
            body.Height >= MinimumHeight,
            $"{path}: der Rumpf ist nur {body.Height}px hoch, erwartet mindestens {MinimumHeight}px.");
    }

    /// <summary>
    /// Alles unter der Topbar, also genau das, was <c>@RenderBody()</c> eingesetzt hat. Gemessen wird
    /// am Rechteck, nicht an <c>offsetParent</c>: ein <c>position:fixed</c>-Element hat keinen, das
    /// wäre also kein Sichtbarkeitstest.
    /// </summary>
    private const string Probe =
        """
        (() => {
          const main = document.getElementById('main');
          const parts = main ? [...main.children].filter(el => !el.classList.contains('topbar')) : [];
          const hidden = parts.filter(el => {
            const style = getComputedStyle(el);
            return style.display === 'none' || style.visibility === 'hidden' || el.hidden;
          });
          return JSON.stringify({
            sections: parts.length,
            hidden: hidden.map(el => el.getAttribute('class') || el.tagName),
            height: Math.round(parts.reduce((sum, el) => sum + el.getBoundingClientRect().height, 0))
          });
        })()
        """;

    private sealed record BodyReport(int Sections, string[] Hidden, int Height);
}
