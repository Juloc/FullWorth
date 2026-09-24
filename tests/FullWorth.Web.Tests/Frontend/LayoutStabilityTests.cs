using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// Was der Browser tatsächlich an Layout-Sprung meldet, je Seite, am Desktop und am Telefon.
///
/// Jeder andere Frontend-Wächter in dieser Suite vergleicht Zeichenketten in Quelldateien. Das fängt
/// eine Regel, die jemand aufgeschrieben hat; es kann keine springende Seite fangen, denn Springen ist
/// eine Eigenschaft des laufenden Browsers und sonst nichts. Dieser hier misst.
///
/// Das Budget unten ist eine Ratsche, kein Ziel. Es steht auf dem, was am Tag der Messung herauskam,
/// und darf nur fallen — eine umgebaute Seite setzt ihren Eintrag herunter und kommt nie zurück. Ein
/// Test, der überall null verlangt hätte, wäre den ganzen Umbau über rot gewesen, und einen dauerhaft
/// roten Test liest niemand.
///
/// Jetzt steht es fast auf null, und das ist kein Ziel, sondern ein Messwert.
/// </summary>
[Trait("Needs", "Browser")]
[Collection(nameof(UiHarnessCollection))]
public sealed class LayoutStabilityTests(UiHarness harness)
{
    /// <summary>
    /// Gemessen am 2026-09-14, desktop / mobil, gegen die Werte vom Anfang des Umbaus:
    ///
    ///   /               0,008 / 0,033   ->   0,000 / 0,000
    ///   /accounts       0,340 / 0,220   ->   0,000 / 0,003
    ///   /transactions   0,056 / 0,096   ->   0,001 / 0,003
    ///   /contracts      0,127 / 0,368   ->   0,000 / 0,000
    ///   /settings       0,020 / 0,036   ->   0,000 / 0,003
    ///   /coach              —           ->   0,000 / 0,003
    ///
    /// Vier Ursachen, in der Reihenfolge, in der sie gefunden wurden:
    ///
    /// 1. Sechzehn Module hängten ihr Stylesheet erst beim ersten Besuch ihrer Ansicht an den Kopf,
    ///    mobile-polish.css sogar erst nach dem ersten Bild. Jede Ansicht zeichnete einmal ungestylt.
    /// 2. Die Breite und der eingeklappte Zustand der Seitenleiste wurden erst nach dem ersten Bild
    ///    wiederhergestellt — wer eingeklappt hatte, sah die Hauptspalte um gut 130px springen.
    /// 3. Die Aktionsleiste oben wuchs mit ihrer Beschriftung, der Privatmodus-Schalter wurde
    ///    nachträglich versteckt, die Zusammenfassung über der Buchungstabelle schob sich davor.
    /// 4. Die Kontenliste wurde gezeichnet und DANACH geschmückt. Eine Zeile wuchs dabei von 73 auf
    ///    125 Pixel. Sie entsteht jetzt außerhalb des Dokuments und wird fertig eingesetzt.
    ///
    /// Zum Vergleich: bei 0,1 hört die Kennzahl auf, eine Seite gut zu nennen. Die schlechteste hier
    /// lag bei 0,368.
    ///
    /// Die 0,005 sind Luft für den CI-Rechner, nicht für neue Sprünge: die schlechteste gemessene
    /// Zahl ist 0,003.
    ///
    /// Woher die 0,003 kommen: die Aktionsleiste oben. Der Aktionsknopf steht im Dokument und wird
    /// erst von JavaScript versteckt, wenn die Seite gar keine Aktion hat — dann wird die Leiste am
    /// Telefon einmal schmaler. Das ist die letzte gemeinsame Quelle und trifft jede Seite ohne
    /// eigene Aktion gleich; sie zu beseitigen heißt, den Kopf umzubauen, nicht eine Seite.
    ///
    /// Coach kam zuerst mit 0,033 herein, und das waren zwei echte Fehler: die drei Vorschläge
    /// entstanden erst beim Öffnen (224px) und der Kennzahlenkasten erst nach dem Abruf (182px).
    /// Die Vorschläge werden jetzt beim Start gezeichnet, das Raster steht im Markup und bekommt
    /// nur noch seine Werte.
    /// </summary>
    private static readonly (string Path, double Desktop, double Mobile)[] Budget =
    [
        //                 desktop  mobile      gemessen        was sich noch bewegt
        ("/",                0.0,    0.0), // 0.000 / 0.000   nichts
        ("/accounts",        0.0,    0.0), // 0.000 / 0.003   Beschriftung der zwei Kopfknöpfe
        // Die Kontodetails hatten als Ansicht der Hülle keine Adresse, die man hätte laden können -
        // gemessen wurde hier also nie etwas (#154).
        ("/accounts/detail", 0.0,    0.0),
        // War hier als "0.001 / 0.003, Beschriftung des Aktionsknopfs" notiert; das stimmt nicht mehr -
        // nachgemessen (53 Läufe: 23 vor einer CSS-Korrektur, 30 danach) zeigt der Sprung nur noch am
        // Telefon, bimodal zwischen 0,000 (haeufiger) und immer genau 0,007 (nie ein anderer Wert). Der
        // wirkliche Verursacher, aus den Quellen des Browsers selbst: .table-panel::after (der
        // Glanzschimmer aus styles/design-depth.css, der der Kartenliste in der Groesse folgt) wuchs von
        // 469 auf 607px Hoehe, weil txSkeletonRows() sieben gleich hohe Platzhalterzeilen zeichnet, die
        // echte Liste aber Zeilen UND eigene Tagesueberschriften hat - eine andere Form, nicht nur eine
        // andere Zahl. Eine echte Teilursache war behoben: .tx-sk-avatar in page.css stand auf 38px,
        // obwohl das echte Symbol (.fw-ident, styles/app.css) 42px bzw. 44px unter 768px misst - jede
        // Zeile war dadurch ein paar Pixel niedriger als sie sein wuerde. Behoben (jetzt 42/44px), aber
        // der Rest der Luecke - eine Zeile mit Tagesueberschriften ist einfach eine andere Form als
        // sieben gleiche Platzhalter - bleibt: dieselbe Klasse Eigenschaft wie bei /admin und /tax oben,
        // nicht vorab wissbar, bevor die Antwort da ist. Die zweite, kleinere, unveraenderte Quelle ist
        // die untere Navigationsleiste beim Sprachwechsel Deutsch->en-US (dieselbe wie beim allgemeinen
        // 1,4e-5-Fund oben, hier nur zwei Zellen breiter als anderswo).
        //
        // 2026-09-21 auf 0,008 korrigiert, und das ist eine Korrektur der Schranke, keine Lockerung
        // der Seite: 0,007 war auf den gemessenen Wert SELBST gesetzt, ohne einen Schritt Spielraum.
        // Ueber 6 gezielte Laeufe liest die Seite fuenfmal 0,007 und einmal 0,008 - derselbe Sprung,
        // nur auf die dritte Stelle anders gerundet. Der Test war damit in etwa jedem sechsten Lauf
        // rot, ohne dass sich an der Seite etwas geaendert haette, und ein Waechter, der zufaellig
        // rot wird, bringt niemandem etwas bei.
        //
        // Die Meldung nennt dabei KEIN verschobenes Element (culprits ist leer) - der Browser
        // schreibt den Sprung keinem Knoten zu. Das passt zu den beiden bekannten Quellen oben
        // (Tagesueberschriften statt gleicher Platzhalter, untere Leiste beim Sprachwechsel) und
        // nicht zu einer neuen.
        //
        // Ueber der Schranke liegen heisst weiterhin: nachsehen. 0,008 ist der beobachtete Hoechstwert,
        // genauso wie /coach und /admin unten ueber ihrem gemessenen Maximum stehen - das ist die
        // Konvention dieser Tabelle, und /transactions war die eine Zeile, die sie nicht einhielt.
        ("/transactions",    0.0,    0.008), // 0.007-0.008 mobil ueber 6 Laeufe
        ("/contracts",       0.0,    0.0), // 0.000 / 0.000   nichts
        ("/settings",        0.0,    0.0), // 0.000 / 0.003   Überschrift bricht am Telefon um
        // Scheibe 14 gab /coach zum ersten Mal eine echte Fixture (Konversation, Ausgaben-Review) statt
        // ihres Leerzustands - der alte 0,000-Eintrag maß also nie die echte Seite. Über 13 Läufe lag
        // der Sprung zwischen 0,000 (er passt oft in ein Bild) und 0,020 / 0,028 (Chat-Antwort und
        // Review-Karte kommen manchmal erst im nächsten Bild) - dieselbe Kopfleiste wie oben trägt
        // ihren Teil bei, der Rest ist die neu sichtbare Chat-/Review-Fläche.
        // Seit #154 ist Coach eine eigene Adresse, und damit misst diese Zeile etwas anderes als
        // vorher: als Ansicht in der Hülle war die Seite beim Umschalten längst fertig geladen, nur
        // der letzte Nachschlag fiel noch auf. Beim Laden einer echten Seite kommt alles auf einmal -
        // Unterhaltung, Ausgaben-Review, Signale -, und die Chat-Fläche wächst dabei von 340 px auf
        // 568 px.
        //
        // Ein echter Fehler steckte darin und ist behoben: renderStarters() zeichnete die Vorschläge
        // sofort, und appendMessage versteckte sie wieder, sobald die geladene Unterhaltung ihre
        // erste Nachricht brachte. Ein Block, der erscheint und wieder verschwindet. Er wird jetzt
        // erst gezeichnet, wenn feststeht, ob es etwas anzubieten gibt.
        //
        // Was bleibt, ist bimodal wie bei /admin und /tax - über je drei Läufen 0,051 am Schreibtisch
        // und 0,285 oder 0,411 am Telefon, je nachdem, ob die Antworten noch ins erste Bild fallen.
        // Das zu beseitigen hiesse, für eine Unterhaltung Platz zu reservieren, deren Länge niemand
        // vorher kennt.
        //
        // Die Zahl am Telefon streut: über zehn Läufen 0,285, 0,411 und einmal 0,434, Letzteres unter
        // Last. Das Budget liegt bewusst über diesem Höchstwert - ein Wächter, der jeden zehnten Lauf
        // grundlos rot wird, wird abgeschaltet und schützt dann gar nichts mehr.
        // Der CI-Rechner misst am Schreibtisch 0,059, wo dieser hier 0,051 liest - dieselbe Seite,
        // andere Maschine. Die Schranke steht ueber BEIDEN, sonst ist sie eine Eigenschaft des
        // Rechners und nicht der Seite.
        ("/coach",           0.065,  0.440), // 0.051-0.059 / 0.285-0.434   Unterhaltung + Review + Signale
        // Ab hier neu in Scheibe 14: erst mit echten Fixtures (ops/ui-harness/fixtures.js) gemessen,
        // vorher zeigte die Harness hier nur den Leerzustand und ein Sprung dort hätte nichts bedeutet.
        // admin: über 23 Läufen (13 davon vor dieser Zeile, 10 danach zur Gegenprobe) stabil bimodal -
        // entweder 0,000 oder genau der Wert unten, nie dazwischen und nie darüber. Nachgemessen mit
        // einem MutationObserver: refresh()s fünf parallele Aufrufe (loadOverview, loadUsers,
        // loadProviders, instanceSettings.load, vault.load) sind bereits alle gleichzeitig unterwegs -
        // das Bimodale kommt davon, ob ihre Antworten alle noch vor dem ersten Bild ankommen oder nicht,
        // nicht von einem Fehler im Code. Nichts hier zu beheben, nur zu messen.
        ("/admin",           0.102,  0.095), // 0.000/0.102 · 0.000/0.095   fünf parallele Ladevorgänge
        ("/audit",           0.0,    0.0),   // 0.000 / 0.000   nichts
        ("/merchants",       0.0,    0.0),   // 0.000 / 0.000   nichts
        ("/rules",           0.0,    0.0),   // 0.000 / 0.000   nichts
        // tax: war die unruhigste Messung hier - über 13 Läufen lag der Sprung zwischen 0,000, ~0,057
        // (ein Bild verzögert) und einmal 0,420 / zweimal ~0,380. Der Fehler war real: page.js's
        // loadData() lud Übersicht und Kandidatenliste parallel, wartete deren Promise.all ab, malte sie
        // - und erst DANACH, in einem eigenen await, holte review-extra.js's renderTaxYearPanel() noch
        // den Jahresprüfungs-Block per eigenem Fetch nach. Der kam garantiert einen Schritt zu spät, weil
        // er erst startete, wenn der Rest schon gezeichnet und angezeigt war - das war die ~0,057-Stufe.
        // Behoben, indem der dritte Abruf ins selbe Promise.all wandert und renderTaxYearPanel() nur noch
        // die schon geladenen Daten zeichnet, synchron mit dem Rest. Gegenprobe über 15 Läufe danach: die
        // 0,057-Stufe kam kein einziges Mal wieder, übrig blieb nur noch 0,000 oder genau 0,392 / 0,380 -
        // derselbe bimodale Rest wie bei admin (Übersicht + Aufschlüsselung + Jahres-Check + Liste sind
        // grosse, anfangs leere Flächen; kommen sie nach dem ersten Bild, verschiebt sich viel auf
        // einmal). Das zu beseitigen hiesse, für all das im Skelett schon Platz zu reservieren, ohne zu
        // wissen, wie viel echte Daten am Ende brauchen - eine echte Weiterentwicklung, kein Bugfix mehr.
        ("/tax",             0.395,   0.385),
        ("/networth",        0.0,    0.0),
        // Ab hier: die in #154 Phase C umgezogenen Seiten. Sie standen vorher nicht hier, weil es sie
        // als eigene Adresse nicht gab - in der alten Hülle war jede von ihnen nur eine Ansicht, die
        // beim Umschalten sichtbar wurde, und was dabei sprang, sah der Benutzer nie beim Laden.
        ("/notifications",   0.0,    0.0),
        ("/categories",      0.0,    0.0),
        ("/collections",     0.0,    0.0),
        ("/analytics",       0.0,    0.0),
        ("/pension",         0.0,    0.0),
        // Die Unterseiten der Einstellungen (#154). Sie standen nie in dieser Tabelle, weil sie als
        // Ansicht in der Hülle keine eigene Adresse hatten, die man hätte laden können - gemessen
        // wurde hier also nie etwas, nicht "gemessen und für gut befunden".
        //
        // Zwei sind bei 0,000. Die vier anderen teilen sich dieselbe Ursache: sie holen beim Laden
        // einen Zustand vom Server - erkannte Dateien, Anbieterkacheln, bisherige Importe, angelegte
        // Schlüssel - und füllen damit Flächen, die vorher leer sind. Die Höhe hängt daran, wie viel
        // zurückkommt; dafür Platz zu reservieren hieße raten, wie viele Importe jemand hat.
        //
        // Über je drei Läufen stabil. Die Schranke liegt über dem beobachteten Höchstwert und lässt
        // Luft für den CI-Rechner: der misst /coach am Schreibtisch 0,059, wo diese Maschine 0,051
        // liest, und genau daran ist die vorige Fassung dieser Tabelle rot geworden.
        ("/settings/security/passkeys",      0.025, 0.055), // 0.015-0.017 / 0.041-0.046
        ("/settings/intelligence",           0.0,   0.0),   // 0.000 / 0.000   nichts
        ("/settings/import",                 0.060, 0.090), // 0.051 / 0.081
        ("/settings/import/broker-pdf",      0.010, 0.045), // 0.004 / 0.037
        // Diese eine streut deutlich: dreimal 0,240, dann 0,270. Die Schranke steht entsprechend
        // weiter oben - lieber grosszuegig auf einer Seite, deren Hoehe an der Zahl der bisherigen
        // Importe haengt, als ein Waechter, der gelegentlich ohne Anlass rot wird.
        ("/settings/import/finanzguru/xlsx", 0.095, 0.300), // 0.074-0.086 / 0.240-0.270
        ("/settings/bank-connections",       0.0,   0.0),   // 0.000 / 0.000   nichts
        // compensation: kam mit 0,230 / 0,042 herein, jetzt 0,062 / 0,015 - über je drei Läufen auf
        // die dritte Stelle konstant, nicht bimodal wie /admin und /tax. Zwei echte Fehler steckten
        // darin und sind behoben (siehe Compensation/Index.cshtml): vier Module hängten ihren Reiter
        // per insertAdjacentHTML in eine schon gezeichnete Leiste, und die Plakette im Kopf bekam das
        // Steuerjahr nachträglich angehängt, wurde dadurch breiter und drückte den Absatz daneben in
        // eine zusätzliche Zeile.
        //
        // Was bleibt, ist das Ergebnis der Berechnung selbst: die Kennzahlen rechts stehen anfangs
        // leer und bekommen Zahlen, deren Breite niemand vorher kennt (313x323 -> 350x307). Dafür
        // Platz zu reservieren hiesse, eine Breite zu raten - das wäre kein Bugfix mehr, sondern
        // eine Festlegung darüber, wie breit ein Gehalt aussehen darf.
        ("/compensation",    0.065,  0.020)
    ];

    public static TheoryData<string, bool> Pages()
    {
        var data = new TheoryData<string, bool>();
        foreach (var (path, _, _) in Budget)
        {
            data.Add(path, false);
            data.Add(path, true);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task A_page_does_not_shift_more_than_its_budget(string path, bool mobile)
    {
        var budget = Budget.Single(entry => entry.Path == path);
        var allowed = mobile ? budget.Mobile : budget.Desktop;

        var (score, culprits) = await harness.MeasureAsync(path, mobile);

        // Auf drei Stellen gerundet verglichen, also genau auf das, was die Meldung ausgibt.
        //
        // Der Grund ist kein Nachlassen, sondern eine gemessene Zahl. Das Dokument trägt die
        // deutschen Beschriftungen, die der Generator hineinschreibt; bei Locale=en-US tauscht i18n
        // sie nach dem Laden aus. In der unteren Leiste stehen sie in Zellen fester Breite, der Text
        // zentriert sich also neu und sonst bewegt sich nichts. Genau beziffert: Schreibtisch
        // 0,000000000, Telefon 0,000013782 - auf allen sechs Adressen derselbe Wert.
        //
        // Versucht und wieder entfernt: die Sprachdatei in app/boot.js vorladen und das 'no-store'
        // beim Abruf fallenlassen. Beides ändert nichts, weil nicht der Abruf zu spät ist, sondern
        // der erste Anstrich vor dem aufgeschobenen Modul liegt.
        //
        // Für die Razor-Seiten aus #154 ist genau das inzwischen gelöst: sie holen ihren Text
        // serverseitig (LocaleText), und die Sprache steht vor dem ersten Anstrich fest. Was hier
        // bleibt, ist die untere Leiste der alten Hülle - und die verschwindet mit ihr. Für 1,4e-5
        // lohnt es nicht, sie vorher noch einmal anzufassen.
        //
        // Der kleinste echte Sprung, der in diesem Umbau gemessen wurde, war 0,001. Der fällt durch.
        Assert.True(
            Math.Round(score, 3) <= allowed,
            $"{path} ({(mobile ? "mobile" : "desktop")}) shifted {score:F3}, budget {allowed:F3}. "
            + $"Moved:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", culprits)}");
    }
}

/// <summary>
/// Die ui-harness, die das echte wwwroot gegen Fixtures ausliefert, plus ein Browser. Einmal für die
/// ganze Klasse gestartet — beides ist teuer und keines trägt Zustand von Seite zu Seite.
/// </summary>
public sealed class UiHarness : IAsyncLifetime
{
    private const int Port = 8096;
    private static readonly string Root = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private Process? _server;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        // Eigener Port, als Argument übergeben: auf einem Entwicklungsrechner läuft meistens schon
        // einer auf der 8095, und der Test darf ihn weder übernehmen noch von ihm abhängen.
        _server = Process.Start(new ProcessStartInfo("node", $"ops/ui-harness/server.mjs {Port}")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start the ui-harness.");

        await WaitForHarnessAsync();

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
        if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true);
        _server?.Dispose();
    }

    /// <summary>
    /// Öffnet die Seite und liefert die Summe der Layout-Sprünge plus das, was sich bewegt hat, je als
    /// "Element von -> nach". Die Rechtecke sind das, was eine Fehlermeldung brauchbar macht: ein Name
    /// allein sagt, dass sich etwas bewegt hat, die zwei Kästen sagen, ob es gewachsen ist, ob etwas
    /// darüber eingefügt wurde oder ob es zur Seite gerutscht ist.
    ///
    /// Sprünge nach einer Eingabe zählen nicht, genau wie die Kennzahl es definiert.
    /// </summary>
    public async Task<(double Score, string[] Culprits)> MeasureAsync(string path, bool mobile)
    {
        var page = await OpenAsync(path, mobile, ShiftProbe);
        await using var context = page.Context;

        var raw = await page.EvaluateAsync<JsonElement>("JSON.stringify(window.__shifts)");
        var shifts = JsonSerializer.Deserialize<Shift[]>(raw.GetString()!, JsonSerializerOptions.Web)!;

        return (
            shifts.Sum(shift => shift.Value),
            shifts.SelectMany(shift => shift.Sources).Distinct().Take(8).ToArray());
    }

    /// <summary>
    /// Dieselbe Seite unter denselben Bedingungen, aber mit einer eigenen Frage. Der Ausdruck läuft
    /// im Browser, wenn alles steht, und liefert eine Zeichenkette zurück.
    /// </summary>
    public async Task<string> AskAsync(string path, bool mobile, string script)
    {
        var page = await OpenAsync(path, mobile, null);
        await using var context = page.Context;

        return await page.EvaluateAsync<string>(script);
    }

    /// <summary>
    /// Eine fertig geladene Seite. Feste Sprache, feste Größe, und derselbe Moment für jede Messung:
    /// die Harness beantwortet die Abrufe sofort, die aufgeschobenen Module brauchen trotzdem einen
    /// Augenblick.
    /// </summary>
    private async Task<IPage> OpenAsync(string path, bool mobile, string? initScript)
    {
        var context = await _browser!.NewContextAsync(new()
        {
            ViewportSize = mobile ? new() { Width = 375, Height = 812 } : new() { Width = 1280, Height = 900 },
            // Feste Sprache, und zwar die, in der das Dokument NICHT erzeugt wird.
            //
            // Ohne das misst jeder Rechner etwas anderes: der Generator schreibt die Beschriftungen
            // auf Deutsch ins Dokument, und wessen Browser Englisch meldet, dem tauscht i18n sie nach
            // dem Laden aus. Auf meinem Rechner (Deutsch) war alles null, auf CI (en-US) bewegte sich
            // in jeder der sechs Adressen die untere Leiste. Ein Test, der nur auf einer Maschine
            // grün ist, ist keiner - also die schwierigere Sprache, überall.
            Locale = "en-US"
        });
        var page = await context.NewPageAsync();
        if (initScript is not null) await page.AddInitScriptAsync(initScript);

        await page.GotoAsync($"http://127.0.0.1:{Port}{path}", new() { WaitUntil = WaitUntilState.NetworkIdle });
        // Hier legt sich die späte Arbeit: verzögerte Module und die Abrufe, die die Harness sofort
        // beantwortet.
        await page.WaitForTimeoutAsync(1500);

        return page;
    }

    /// <summary>
    /// Vor allem anderen eingebaut: ein später hinzugefügter Beobachter verpasst die Sprünge, die
    /// passieren, während die Seite sich noch zusammensetzt — also alle interessanten.
    /// </summary>
    private const string ShiftProbe =
        """
            window.__shifts = [];
            // className ist an einem SVG ein SVGAnimatedString, kein Text - der Bericht sagte dann
            // "[object SVGAnimatedString]" und half niemandem. Das Attribut lesen, nicht die
            // Eigenschaft, und den Elternnamen dazu, damit die Stelle auffindbar ist.
            const name = node => {
              if (!node) return '?';
              const own = node.id || node.getAttribute?.('class') || node.tagName;
              const parent = node.parentElement;
              const around = parent && (parent.id || parent.getAttribute?.('class'));
              return around ? around + ' > ' + own : own;
            };
            const box = rect => Math.round(rect.x) + ',' + Math.round(rect.y)
              + ' ' + Math.round(rect.width) + 'x' + Math.round(rect.height);
            new PerformanceObserver(list => {
              for (const entry of list.getEntries()) {
                if (entry.hadRecentInput) continue;
                window.__shifts.push({
                  value: entry.value,
                  sources: (entry.sources || []).map(
                    source => name(source.node) + ' ' + box(source.previousRect) + ' -> ' + box(source.currentRect))
                });
              }
            }).observe({ type: 'layout-shift', buffered: true });
            """;

    private sealed record Shift(double Value, string[] Sources);

    /// <summary>
    /// Warten, bis UNSERE Harness antwortet — nicht bis irgendjemand antwortet.
    ///
    /// Vorher stand hier eine reine TCP-Probe. Lief auf dem Port schon ein anderer Server (die
    /// Cloud-Harness stand auf demselben), band node nicht, starb still, und die Probe war trotzdem
    /// erfolgreich: der ganze Test maß dann die falsche Anwendung und meldete ihre Zahlen als die
    /// dieser. Eine Messung, die das Falsche misst, ist schlimmer als gar keine.
    /// </summary>
    private async Task WaitForHarnessAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (_server is { HasExited: true })
                throw new InvalidOperationException(
                    $"Die ui-harness ist sofort beendet. Meistens hält schon etwas anderes Port {Port} — "
                    + "in diesem Ordner: " + await _server.StandardError.ReadToEndAsync());

            try
            {
                var answer = await client.GetStringAsync($"http://127.0.0.1:{Port}/__harness");
                if (answer.Contains("\"harness\":\"fullworth\"", StringComparison.Ordinal)) return;

                throw new InvalidOperationException(
                    $"Auf Port {Port} antwortet etwas anderes als diese ui-harness: {answer}");
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100);
            }
            catch (TaskCanceledException)
            {
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException($"The ui-harness did not come up on {Port}.");
    }
}

[CollectionDefinition(nameof(UiHarnessCollection))]
public sealed class UiHarnessCollection : ICollectionFixture<UiHarness>;
