using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

public sealed class FrontendArchitectureGuardTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string WwwRoot() => Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot");

    private static IEnumerable<(string Relative, string Content)> JavaScriptFiles()
    {
        var root = WwwRoot();
        foreach (var file in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            yield return (relative, File.ReadAllText(file));
        }
    }

    private static void AssertNoNewViolations(
        Regex pattern,
        IReadOnlySet<string> allowed,
        string rule)
    {
        var offenders = JavaScriptFiles()
            .Where(file => pattern.IsMatch(file.Content) && !allowed.Contains(file.Relative))
            .Select(file => file.Relative)
            .OrderBy(x => x)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{rule}{Environment.NewLine}Unexpected files:{Environment.NewLine}{string.Join(Environment.NewLine, offenders.Select(x => " - " + x))}");
    }

    [Fact]
    public void OnlySharedDialogModuleMayIntroduceNewNativeDialogs()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "components/dialog.js"
        };

        AssertNoNewViolations(
            new Regex(@"createElement\s*\(\s*['""]dialog['""]\s*\)", RegexOptions.Compiled),
            allowed,
            "Create dialogs through components/dialog.js. Do not add feature-local native dialog factories.");
    }

    [Fact]
    public void NoNewFeatureMayCallBffDirectly()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "core/api.js"
        };

        AssertNoNewViolations(
            new Regex(@"/bff/(backend|banking)(?:/|\b)", RegexOptions.Compiled),
            allowed,
            "New BFF calls must go through core/api.js.");
    }

    [Fact]
    public void NoNewGlobalFetchMonkeyPatches()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // security/browser-fetch.js stood here. Since #154 retired index.html nothing loaded it, and it
            // is gone; core/api.js reaches the antiforgery token through security/secure-fetch.js.
        };

        AssertNoNewViolations(
            new Regex(@"window\.fetch\s*=", RegexOptions.Compiled),
            allowed,
            "Do not add new global fetch monkey patches.");
    }

    [Fact]
    public void NoNewNativeConfirmCalls()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);

        AssertNoNewViolations(
            new Regex(@"(?<![\.\w])confirm\s*\(|window\.confirm\s*\(", RegexOptions.Compiled),
            allowed,
            "Use components/confirm.js instead of native confirm().");
    }

    [Fact]
    public void NoNewGlobalDomPatchObservers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // Shared infrastructure observers are explicitly reviewed and scoped.
            "components/accessibility-release.js",
            // app/motion.js stood here: it made figures count up by watching their text. Since #154
            // retired index.html no page loaded it, and figures standing at once is the intended
            // state - one paint, no change after it - so it is gone rather than restored.
            // app/appearance.js used to be here: it rebuilt the "Farben" settings panel via DOM
            // injection and kept it in sync with a MutationObserver (Issue #149). The panel is now
            // static markup in pages/settings/page.html, wired once like every other settings
            // control - nothing left in appearance.js observes the DOM any more.
        };

        AssertNoNewViolations(
            new Regex(@"new\s+MutationObserver\s*\(", RegexOptions.Compiled),
            allowed,
            "Do not add MutationObserver-based feature repair/decorating layers.");
    }

    [Fact]
    public void NoNewPatchLayerFileNames()
    {
        var featureRoot = Path.Combine(WwwRoot(), "features");
        var allowed = new HashSet<string>(StringComparer.Ordinal);

        var offenders = Directory.EnumerateFiles(featureRoot, "*.js", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Where(name =>
                name!.EndsWith("-installer.js", StringComparison.Ordinal) ||
                name.Contains("-final-ui", StringComparison.Ordinal) ||
                name.Contains("-parity-ui", StringComparison.Ordinal) ||
                name.Contains("-completion-ui", StringComparison.Ordinal))
            .Where(name => !allowed.Contains(name!))
            .OrderBy(name => name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Do not add new installer/final/parity/completion patch layers.{Environment.NewLine}{string.Join(Environment.NewLine, offenders.Select(x => " - " + x))}");
    }

    [Fact]
    public void MainShellDoesNotLoadDeletedPatchModules()
    {
        var html = WebSources.Layout();
        Assert.DoesNotContain("/features/accounts-ux.js", html);
        Assert.DoesNotContain("/features/compensation-nav.js", html);
        Assert.DoesNotContain("/parity-completion.css", html);
    }

    [Fact]
    public void SharedCssLayersAreExplicitAndOrdered()
    {
        var html = WebSources.Layout();
        var tokens = html.IndexOf("/styles/tokens.css", StringComparison.Ordinal);
        var reset = html.IndexOf("/styles/reset.css", StringComparison.Ordinal);
        var appearance = html.IndexOf("/styles/appearance.css", StringComparison.Ordinal);
        var shell = html.IndexOf("/styles/shell.css", StringComparison.Ordinal);
        var components = html.IndexOf("/styles/components.css", StringComparison.Ordinal);
        var featureBase = html.IndexOf("/styles/app.css", StringComparison.Ordinal);

        Assert.True(tokens >= 0 && reset > tokens && appearance > reset && shell > appearance && components > shell && featureBase > components);
        Assert.True(File.Exists(Path.Combine(WwwRoot(), "styles", "tokens.css")));
        Assert.True(File.Exists(Path.Combine(WwwRoot(), "styles", "reset.css")));
        Assert.True(File.Exists(Path.Combine(WwwRoot(), "styles", "shell.css")));
        Assert.True(File.Exists(Path.Combine(WwwRoot(), "styles", "components.css")));
        Assert.False(File.Exists(Path.Combine(WwwRoot(), "parity-completion.css")));
    }

    [Fact]
    public void SettingsWorkflowsStayOutOfAppBootstrap()
    {
        var app = WebSources.Asset("app", "shell.js");
        var settings = File.ReadAllText(Path.Combine(WwwRoot(), "pages", "settings", "page.js"));

        Assert.DoesNotContain("openDeleteAccountDialog", app);
        Assert.DoesNotContain("openTwoFactorDialog", app);
        Assert.DoesNotContain("renderSharing(", app);

        // Seit #154 heisst der Name dieses Tests woertlich, was er prueft: die Einstellungen werden
        // von ihrer eigenen Seite verdrahtet und nicht mehr beim Start der Huelle. Die beiden Zeilen
        // darueber bleiben: sie halten die Dialoge dort, wo sie hingehoeren.
        var entry = File.ReadAllText(Path.Combine(WwwRoot(), "pages", "settings", "entry.js"));
        Assert.DoesNotContain("bindSettings(ctx)", app);
        Assert.Contains("bindSettings(context)", entry);
        Assert.Contains("renderSettings(context", entry);

        Assert.Contains("openDeleteAccountDialog", settings);
        Assert.Contains("openTwoFactorDialog", settings);
        Assert.Contains("renderSharing(ctx)", settings);
    }

    [Fact]
    public void BootstrapLivesInApp_NotInFeatureOwners()
    {
        var app = WebSources.Asset("app", "shell.js");
        var shell = File.ReadAllText(Path.Combine(WwwRoot(), "app", "shell.js"));
        var accounts = File.ReadAllText(Path.Combine(WwwRoot(), "pages", "accounts", "page.js"));

        // Die Seitenleiste gehoert seit #154 der geteilten Huelle, nicht mehr app.js allein: eine
        // Razor-Seite laedt app.js nicht, und dort war der Einklapp-Knopf deshalb tot. Die Regel ist
        // dieselbe geblieben - der Start gehoert der Huelle und keiner Seite -, nur umfasst "die
        // Huelle" jetzt beide Dateien.
        Assert.Contains("initResizableSidebar();", shell);
        Assert.Contains("syncResponsiveSidebar();", shell);
        // Gestartet wird seit #154 ueber den Einstieg der jeweiligen Seite - startShellPage ist das
        // boot() von frueher, nur je Seite statt einmal fuer alle.
        Assert.Contains("export async function startShellPage", shell);

        Assert.DoesNotContain("initResizableSidebar();", accounts);
        Assert.DoesNotContain("syncResponsiveSidebar();", accounts);
        Assert.DoesNotContain("startShellPage", accounts);
    }

    [Fact]
    public void AccountsPresentationUsesSharedCoreWithoutPatchObserverOrSyntheticNavigation()
    {
        var accounts = File.ReadAllText(Path.Combine(WwwRoot(), "pages", "accounts", "presentation.js"));
        Assert.DoesNotContain("/bff/", accounts);
        Assert.DoesNotContain("new MutationObserver", accounts);
        Assert.DoesNotContain(".click()", accounts);
        Assert.DoesNotContain("fwNavScope", accounts);
        Assert.Contains("apiClient.backend", accounts);
        Assert.Contains("navigate(", accounts);
        Assert.Contains("bindAccountsPresentation", accounts);
        Assert.DoesNotContain("onAppEvent(", accounts);
        Assert.DoesNotContain("pools(", accounts);
        Assert.DoesNotContain("groupFromHead", accounts);
        Assert.Contains("[data-account-id]", accounts);
        // Die Bankverbindungen zeichnet seit #125 ihre eigene Seite unter den Einstellungen;
        // presentation.js dekoriert nur noch Konten und Gruppen.
        Assert.DoesNotContain("[data-connection-id]", accounts);
        Assert.Contains("[data-group-id]", accounts);
    }

    [Fact]
    public void NoGlobalFeatureNavigationBridgeReturns()
    {
        var app = WebSources.Asset("app", "shell.js");
        Assert.DoesNotContain("window.fwNavScope", app);
        Assert.DoesNotContain("window.fwOpenBudget", app);
        Assert.DoesNotContain("window.fwSyncResponsiveSidebar", app);
        Assert.DoesNotContain("window.fwClampSidebarWidth", app);
        Assert.Contains("installNavigation", app);
    }
}
