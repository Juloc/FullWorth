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
/// und darf nur fallen — eine umgebaute Seite setzt ihren Eintrag auf null und kommt nie zurück. Ein
/// Test, der überall null verlangte, wäre den ganzen Umbau über rot, und einen dauerhaft roten Test
/// liest niemand.
/// </summary>
[Collection(nameof(UiHarnessCollection))]
public sealed class LayoutStabilityTests(UiHarness harness)
{
    /// <summary>
    /// Neu gemessen am 2026-09-13, nachdem die Hülle aus einer Menüquelle entsteht und kein Stylesheet
    /// mehr nach dem Zeichnen nachgeladen wird.
    ///
    /// Was der Umbau gebracht hat, auf demselben Rechner gemessen:
    ///
    ///   /               0,008 / 0,033   ->   0,000 / 0,000
    ///   /accounts       0,340 / 0,220   ->   0,320 / 0,183
    ///   /transactions   0,056 / 0,096   ->   0,000 / 0,003
    ///   /contracts      0,127 / 0,368   ->   0,117 / 0,310
    ///   /settings       0,020 / 0,036   ->   0,000 / 0,003
    ///
    /// Drei der fünf Seiten stehen am Desktop still, gemessen null. Was bleibt, sind /accounts und
    /// /contracts, und beide aus demselben Grund: sie schieben ihre Zeilen und Karten erst nach der
    /// Antwort des Servers in eine bereits gezeichnete Seite. Das löst nicht die Hülle, sondern der
    /// Umzug dieser Seiten — danach steht auch hier eine Null.
    ///
    /// Zum Vergleich: bei 0,1 hört die Kennzahl auf, eine Seite gut zu nennen.
    /// </summary>
    private static readonly (string Path, double Desktop, double Mobile)[] Budget =
    [
        //                 desktop  mobile      gemessen        was sich bewegt
        ("/",                0.005,  0.005), // 0.000 / 0.000   nichts mehr
        ("/accounts",         0.33,   0.19), // 0.320 / 0.183   Zeilen und Symbole je Konto
        ("/transactions",    0.005,   0.01), // 0.000 / 0.003   Zusammenfassung bricht am Telefon um
        ("/contracts",        0.13,   0.32), // 0.117 / 0.310   vier Felder über der Liste
        ("/settings",        0.005,   0.01)  // 0.000 / 0.003   Überschrift bricht am Telefon um
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
