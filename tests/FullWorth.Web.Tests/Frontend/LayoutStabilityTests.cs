using System.Diagnostics;
using System.Net.Sockets;
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
    /// Zahl ist 0,003, und die kommt vom Umbruch einer Beschriftung am Telefon.
    /// </summary>
    private static readonly (string Path, double Desktop, double Mobile)[] Budget =
    [
        //                 desktop  mobile      gemessen        was sich noch bewegt
        ("/",                0.005,  0.005), // 0.000 / 0.000   nichts
        ("/accounts",        0.005,  0.005), // 0.000 / 0.003   Beschriftung der zwei Kopfknöpfe
        ("/transactions",    0.005,  0.005), // 0.001 / 0.003   Beschriftung des Aktionsknopfs
        ("/contracts",       0.005,  0.005), // 0.000 / 0.000   nichts
        ("/settings",        0.005,  0.005)  // 0.000 / 0.003   Überschrift bricht am Telefon um
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

        Assert.True(
            score <= allowed,
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

        await WaitForPortAsync();

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
        await using var context = await _browser!.NewContextAsync(new()
        {
            ViewportSize = mobile ? new() { Width = 375, Height = 812 } : new() { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();

        // Vor allem anderen eingebaut: ein später hinzugefügter Beobachter verpasst die Sprünge, die
        // passieren, während die Seite sich noch zusammensetzt — also alle interessanten.
        await page.AddInitScriptAsync("""
            window.__shifts = [];
            const name = node => node ? (node.id || node.className || node.tagName) : '?';
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
            """);

        await page.GotoAsync($"http://127.0.0.1:{Port}{path}", new() { WaitUntil = WaitUntilState.NetworkIdle });
        // Hier legt sich die späte Arbeit: verzögerte Module und die Abrufe, die die Harness sofort
        // beantwortet.
        await page.WaitForTimeoutAsync(1500);

        var raw = await page.EvaluateAsync<JsonElement>("JSON.stringify(window.__shifts)");
        var shifts = JsonSerializer.Deserialize<Shift[]>(raw.GetString()!, JsonSerializerOptions.Web)!;

        return (
            shifts.Sum(shift => shift.Value),
            shifts.SelectMany(shift => shift.Sources).Distinct().Take(8).ToArray());
    }

    private sealed record Shift(double Value, string[] Sources);

    private static async Task WaitForPortAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync("127.0.0.1", Port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException($"The ui-harness did not come up on {Port}.");
    }
}

[CollectionDefinition(nameof(UiHarnessCollection))]
public sealed class UiHarnessCollection : ICollectionFixture<UiHarness>;
