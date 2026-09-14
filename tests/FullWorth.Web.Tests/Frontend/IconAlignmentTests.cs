using System.Text.Json;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// Sitzt ein Symbol mittig in seinem Knopf? Gemessen, nicht angesehen.
///
/// Der Anlass war eine einzige fehlende Zeile. <c>.icon-button</c> in styles/shell.css setzte Größe,
/// Rahmen und <c>place-items:center</c> — aber nicht <c>padding</c>. Die Grundschicht gibt JEDEM
/// button 9px 11px, und mit <c>box-sizing:border-box</c> blieben von 36px noch 12px Inhaltsbreite für
/// ein 18px breites Symbol. Ein Rasterelement, das über seine Fläche hinausragt, richtet der Browser
/// am Anfang aus, <c>place-items:center</c> hin oder her: links 12 Pixel Abstand, rechts 6. Das betraf
/// die Kopfzeile auf jeder Seite der Anwendung.
///
/// Kein Wächter dieser Suite konnte das finden, denn keiner von ihnen sieht die Seite — sie vergleichen
/// Zeichenketten in Quelldateien, und „links 12, rechts 6" steht in keiner Datei. Es ist wie der
/// Layout-Sprung eine Eigenschaft des laufenden Browsers.
///
/// Die Regel hier ist bewusst eng gefasst: ein Behälter, dessen einziger Inhalt ein Symbol ist, meint
/// dieses Symbol mittig. Alles mit Text daneben — Menüeinträge, beschriftete Knöpfe — fällt heraus,
/// denn dort gehört das Symbol nach links.
/// </summary>
[Trait("Needs", "Browser")]
[Collection(nameof(UiHarnessCollection))]
public sealed class IconAlignmentTests(UiHarness harness)
{
    /// <summary>
    /// Jeder Behälter, dessen einziges Kind ein svg oder ein img ist und der sonst keinen Text trägt.
    /// Verglichen werden die gegenüberliegenden Abstände: links gegen rechts, oben gegen unten. Die
    /// Rahmenbreite steckt in beiden und hebt sich dabei auf.
    ///
    /// Eine halbe Zeile Toleranz für die Rundung des Browsers; die Werte, um die es geht, sind ganze
    /// Pixel. Der gefundene Fehler lag bei 6.
    /// </summary>
    private const string Script =
        """
        JSON.stringify([...document.querySelectorAll('*')].flatMap(el => {
          if (el.children.length !== 1 || el.textContent.trim()) return [];
          const kid = el.children[0];
          if (!['svg', 'img'].includes(kid.tagName.toLowerCase())) return [];

          const outer = el.getBoundingClientRect(), inner = kid.getBoundingClientRect();
          if (!outer.width || !inner.width) return [];

          const waagerecht = (inner.left - outer.left) - (outer.right - inner.right);
          const senkrecht = (inner.top - outer.top) - (outer.bottom - inner.bottom);
          if (Math.abs(waagerecht) < 1.5 && Math.abs(senkrecht) < 1.5) return [];

          const name = el.id || el.getAttribute('class') || el.tagName;
          return [name + ': links/rechts ' + Math.round(inner.left - outer.left) + '/'
            + Math.round(outer.right - inner.right) + ', oben/unten '
            + Math.round(inner.top - outer.top) + '/' + Math.round(outer.bottom - inner.bottom)];
        }))
        """;

    public static TheoryData<string, bool> Pages()
    {
        var data = new TheoryData<string, bool>();
        foreach (var path in new[] { "/", "/accounts", "/transactions", "/contracts", "/settings", "/coach" })
        {
            data.Add(path, false);
            data.Add(path, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task An_icon_sits_in_the_middle_of_what_holds_it(string path, bool mobile)
    {
        var found = JsonSerializer.Deserialize<string[]>(await harness.AskAsync(path, mobile, Script))!;

        Assert.True(found.Length == 0,
            $"{path} ({(mobile ? "mobile" : "desktop")}): diese Symbole sitzen nicht mittig:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", found));
    }
}
