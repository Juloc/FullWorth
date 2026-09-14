using System.Text.Json;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// Am Telefon ist „Mehr" der einzige Weg zu allem außerhalb der vier schnellen Ziele. Dieser Test
/// öffnet den Bogen und sieht nach, ob wirklich alles darin erreichbar ist.
///
/// Der Anlass: es war es nicht. <c>responsive.css</c> gab der Karte
/// <c>grid-template-rows:auto minmax(0,1fr)</c> und jeder Liste <c>overflow-y:auto</c> — eine Regel für
/// einen Bogen mit genau ZWEI Kindern, wie ihn das Überlaufmenü der Kopfzeile hat. Das Handy-Menü hat
/// elf: den Kopf und fünfmal Überschrift plus Liste. Die Gruppen landeten in impliziten Zeilen, der
/// 1fr-Kasten nahm den Platz, und jede Gruppe wurde ihr eigenes 58 Pixel hohes Rollfenster. Gemessen
/// war Gruppe „Planung" 58 Pixel hoch bei 251 Pixeln Inhalt: sichtbar war EIN Eintrag von fünf.
///
/// Vierzehn von neunzehn Seiten waren damit am Telefon nicht erreichbar, und kein Test hat es gemerkt.
/// <c>MenuParityTests</c> konnte es nicht: es liest den Quelltext, und im Markup standen alle Einträge.
/// Sie waren nur nicht zu sehen — und das weiß nur der Browser.
///
/// Geprüft wird deshalb nicht, ob etwas im Dokument steht, sondern ob es Platz hat: keine Gruppe darf
/// mehr Inhalt haben, als sie zeigt.
/// </summary>
[Trait("Needs", "Browser")]
[Collection(nameof(UiHarnessCollection))]
public sealed class MobileMenuReachabilityTests(UiHarness harness)
{
    private const string Script =
        """
        (async () => {
          document.querySelectorAll('dialog[open]').forEach(d => d.close());
          document.querySelector('#bottom-nav .nav-item:last-child').click();
          await new Promise(r => setTimeout(r, 500));

          const karte = document.querySelector('.more-sheet-dialog .more-sheet');
          if (!karte) return JSON.stringify({ fehler: 'Der Bogen hat sich nicht geoeffnet.' });

          // Eine Gruppe, die mehr Inhalt hat als Hoehe, verbirgt Eintraege hinter einem eigenen
          // Rollbalken. Die Karte selbst DARF rollen - das ist der eine gewollte Rollbereich.
          const beschnitten = [...karte.querySelectorAll('.more-list')]
            .filter(liste => liste.scrollHeight > liste.clientHeight + 1)
            .map(liste => `${liste.previousElementSibling?.textContent.trim() || '?'}: zeigt `
              + `${Math.round(liste.clientHeight)}px von ${liste.scrollHeight}px`);

          const eintraege = karte.querySelectorAll('.more-list button').length;
          return JSON.stringify({ beschnitten, eintraege, karteRollt: karte.scrollHeight > karte.clientHeight });
        })()
        """;

    [Fact]
    public async Task Every_entry_in_the_phone_menu_has_room()
    {
        var roh = await harness.AskAsync("/", mobile: true, Script);
        var befund = JsonSerializer.Deserialize<Befund>(roh, JsonSerializerOptions.Web)!;

        Assert.Null(befund.Fehler);

        // Fuenf Gruppen, neunzehn sichtbare Eintraege gegen die Fixtures (Admin ist ausgeblendet).
        // Die Zahl steht hier, damit ein stiller Verlust auffaellt, nicht als Zielvorgabe.
        Assert.True(befund.Eintraege >= 15,
            $"Der Bogen zeigt nur {befund.Eintraege} Eintraege - das Menue hat deutlich mehr.");

        Assert.True(befund.Beschnitten.Length == 0,
            "Diese Gruppen sind zu klein fuer ihren Inhalt, ihre Eintraege sind hinter einem eigenen "
            + "Rollbalken versteckt:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", befund.Beschnitten));
    }

    private sealed record Befund(string? Fehler, string[] Beschnitten, int Eintraege, bool KarteRollt);
}
