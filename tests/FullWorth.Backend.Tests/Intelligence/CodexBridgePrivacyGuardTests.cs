using System.Text.RegularExpressions;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// #156: was die Codex-Bruecke protokollieren darf - und wie oft sie einen Prozess startet.
///
/// Quelltextpruefungen auf <c>server.mjs</c>, wie die Frontend-Waechter sie fuer .js-Dateien machen:
/// dieses Repo hat keinen JavaScript-Testlaeufer, und was hier gepinnt wird, steht im Text der Datei.
///
/// Zwei Dinge, die ohne diese Datei zurueckfallen:
///
/// 1. Der Prompt stand vollstaendig im Protokoll. Er haengt als Argument an <c>codex exec</c>, und
///    <c>START ${commandForLog(args)}</c> schrieb die ganze Befehlszeile. Bei einem Beleg sind das
///    Haendler, Artikel und Betraege; bei einer Gehaltsabrechnung mehr. In einer Finanzanwendung ist
///    das kein Rauschen, sondern ein Datenschutzproblem - und <c>redact()</c> faengt es nicht, das
///    schwaerzt Tokens, keine Nutzerdaten.
/// 2. Jede Inferenz startete drei Prozesse: <c>--version</c>, <c>login status</c> und erst dann die
///    eigentliche Ausfuehrung. Die ersten beiden beantworteten Fragen, die laengst feststanden.
/// </summary>
public sealed class CodexBridgePrivacyGuardTests
{
    private static string Bridge()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "FullWorth.CodexBridge", "server.mjs"));
    }

    /// <summary>
    /// Die Befehlszeile darf nicht mehr aus den Argumenten zusammengesetzt werden. Genau diese eine
    /// Zeile war das Leck: der Prompt ist eines der Argumente.
    /// </summary>
    [Fact]
    public void The_command_line_is_never_logged_verbatim()
    {
        var source = Bridge();

        // Die alte Fassung: ['codex', ...args].map(...).join(' ')
        Assert.DoesNotContain("['codex', ...args]", source);
        // Werte werden gezaehlt, nicht gezeigt.
        Assert.Contains("Wert(e)", source);
        Assert.Contains("promptChars", source);
    }

    /// <summary>
    /// Bei einer Inferenz IST die Ausgabe das Ergebnis - die erkannten Buchungen, Betraege, Namen.
    /// Sie darf nur dort ins Protokoll, wo sie keine Nutzerdaten enthalten kann, und das muss die
    /// Ausnahme sein, nicht die Regel: ein Standardwert <c>true</c> hiesse, dass jede neue
    /// Aufrufstelle das Leck von selbst wieder aufmacht.
    /// </summary>
    [Fact]
    public void Child_output_is_only_logged_where_it_cannot_carry_user_data()
    {
        var source = Bridge();

        Assert.Contains("logOutput = false", source);
        Assert.Contains("if (logOutput) addLog(", source);

        // Erlaubt ist es genau bei Version, Anmeldestatus, Abmelden und Modellliste - vier Stellen.
        Assert.Equal(4, Regex.Matches(source, @"logOutput: true").Count);
        foreach (var safe in new[] { "'version'", "'status'", "'logout'", "'catalog'" })
            Assert.Matches($@"stage: {safe}[^\n]*logOutput: true", source);
    }

    /// <summary>Der Belegscan protokollierte seinen Prompt zusaetzlich noch einmal ausdruecklich.</summary>
    [Fact]
    public void The_receipt_scan_no_longer_logs_its_prompt()
    {
        var source = Bridge();

        Assert.DoesNotContain("'prompt', prompt,", source);
        // Die Groesse beantwortet "gebaut?" und "zu gross?", ohne den Inhalt zu zeigen.
        Assert.Contains("Prompt built: ${prompt.length}", source);
    }

    /// <summary>
    /// Die Version kann sich waehrend der Laufzeit nicht aendern - die ausfuehrbare Datei liegt im
    /// Abbild. Sie je Inferenz neu zu erfragen war ein Kindprozess fuer eine Antwort, die feststand.
    /// </summary>
    [Fact]
    public void The_codex_version_is_resolved_once_per_process()
    {
        var source = Bridge();

        Assert.Contains("let versionPromise = null;", source);
        Assert.Contains("versionPromise ??=", source);
        // Ein gescheiterter Versuch darf sich nicht festschreiben.
        Assert.Contains("versionPromise = null; return null;", source);
    }

    /// <summary>
    /// Der Anmeldezustand wird zwischengespeichert, aber mit Verfallszeit UND mit Verwerfen an den
    /// drei Stellen, an denen er nachweislich falsch wird. Ein Zwischenspeicher ohne diese drei waere
    /// schlimmer als keiner: er behauptete "angemeldet", bis die Frist ablaeuft.
    /// </summary>
    [Fact]
    public void The_auth_state_is_cached_with_an_expiry_and_dropped_when_it_is_known_to_be_wrong()
    {
        var source = Bridge();

        Assert.Contains("authCacheTtlMs", source);
        Assert.Contains("function invalidateAuth(ownerScope)", source);
        // Abmelden, Anmeldeversuch beendet, und ein Fehlschlag, der nach fehlender Anmeldung aussieht.
        Assert.Equal(3, Regex.Matches(source, @"invalidateAuth\(ownerScope\);").Count);
        Assert.Contains("not logged|not signed|logged out|unauthor", source);
    }
}
