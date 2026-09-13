using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Playwright;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// What the browser actually reports as layout shift, per page, on a desktop and on a phone.
///
/// Every other frontend guard in this suite compares strings in source files. That catches a rule
/// somebody wrote down; it cannot catch a page that jumps, because jumping is a property of the
/// running browser and of nothing else. This one measures it.
///
/// The budget below is a ratchet, not a target. It starts at what was measured on the day it was
/// written and may only ever go down — a page that is rebuilt sets its entry to zero and can never
/// go back. A test that simply demanded zero everywhere would be red for the whole rebuild, and a
/// permanently red test is one nobody reads.
/// </summary>
[Collection(nameof(UiHarnessCollection))]
public sealed class LayoutStabilityTests(UiHarness harness)
{
    /// <summary>
    /// Measured on 2026-09-13 against main over three runs, budget set to the worst observed value
    /// plus a little. Lower is allowed, higher is a regression. Zero means the page has been rebuilt
    /// and must stay still.
    ///
    /// For scale: 0.1 is where the metric stops calling a page good. Two of these are over three
    /// times that, and one culprit appears on every single page — the topbar action button, hidden
    /// and then shown once the view knows whether it has one.
    ///
    /// Seven of the ten numbers repeated to three decimals across runs. Only /transactions moved
    /// (0.012 → 0.056 on desktop), which fits what it does: it prepends a summary bar and a scope
    /// bar once its fetch returns, so the score depends on when that lands relative to paint.
    /// </summary>
    private static readonly (string Path, double Desktop, double Mobile)[] Budget =
    [
        //                 desktop  mobile      worst seen        what moves
        ("/",                 0.02,   0.05), // 0.008 / 0.033   topbar-actions, ::before
        ("/accounts",         0.36,   0.24), // 0.340 / 0.220   identity icons inserted per row
        ("/transactions",     0.08,   0.12), // 0.056 / 0.096   summary and scope bars prepended
        ("/contracts",        0.15,   0.39), // 0.127 / 0.368   four panels above the list
        ("/settings",         0.04,   0.05)  // 0.020 / 0.036   admin row appears in sidebar-foot
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
            + $"Moved: {string.Join(", ", culprits)}");
    }
}

/// <summary>
/// The ui-harness serving the real wwwroot against fixtures, plus a browser. Started once for the
/// whole class — both are expensive and neither carries state between pages.
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
        // Its own port, passed as the argument the harness reads: a developer usually has one
        // running on the default 8095 already, and the test must not take it over or depend on it.
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
    /// Opens the page and returns the summed layout-shift score plus the elements that moved.
    /// Shifts that follow a user interaction are excluded, exactly as the metric defines.
    /// </summary>
    public async Task<(double Score, string[] Culprits)> MeasureAsync(string path, bool mobile)
    {
        await using var context = await _browser!.NewContextAsync(new()
        {
            ViewportSize = mobile ? new() { Width = 375, Height = 812 } : new() { Width = 1280, Height = 900 }
        });
        var page = await context.NewPageAsync();

        // Installed before anything loads: an observer added afterwards misses the shifts that
        // happen while the page is still assembling itself, which are all of the interesting ones.
        await page.AddInitScriptAsync("""
            window.__shifts = [];
            new PerformanceObserver(list => {
              for (const entry of list.getEntries()) {
                if (entry.hadRecentInput) continue;
                window.__shifts.push({
                  value: entry.value,
                  sources: (entry.sources || []).map(s => s.node ? (s.node.id || s.node.className || s.node.tagName) : '?')
                });
              }
            }).observe({ type: 'layout-shift', buffered: true });
            """);

        await page.GotoAsync($"http://127.0.0.1:{Port}{path}", new() { WaitUntil = WaitUntilState.NetworkIdle });
        // Late work settles here: deferred modules, fetches the harness answers instantly, and the
        // stylesheets this app still appends after paint.
        await page.WaitForTimeoutAsync(1500);

        var raw = await page.EvaluateAsync<JsonElement>("JSON.stringify(window.__shifts)");
        var shifts = JsonSerializer.Deserialize<Shift[]>(raw.GetString()!, JsonSerializerOptions.Web)!;

        return (
            shifts.Sum(shift => shift.Value),
            shifts.SelectMany(shift => shift.Sources).Distinct().Take(6).ToArray());
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
