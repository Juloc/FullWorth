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
            "ui/dialog.js",
            // Legacy migration allow-list. This list may only shrink.
            "features/wealth-specialized-assets.js"
        };

        AssertNoNewViolations(
            new Regex(@"createElement\s*\(\s*['""]dialog['""]\s*\)", RegexOptions.Compiled),
            allowed,
            "Create dialogs through ui/dialog.js. Do not add feature-local native dialog factories.");
    }

    [Fact]
    public void NoNewFeatureMayCallBffDirectly()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal);

        AssertNoNewViolations(
            new Regex(@"/bff/(backend|banking)/", RegexOptions.Compiled),
            allowed,
            "New BFF calls must go through core/api.js.");
    }

    [Fact]
    public void NoNewGlobalFetchMonkeyPatches()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "security/browser-fetch.js",
        };

        AssertNoNewViolations(
            new Regex(@"window\.fetch\s*=", RegexOptions.Compiled),
            allowed,
            "Do not add new global fetch monkey patches.");
    }

    [Fact]
    public void NoNewNativeConfirmCalls()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // Legacy migration allow-list. This list may only shrink.
            "admin/admin.js",
            "features/access-setup.js",
            "features/compensation-extended.js",
            "features/compensation-history.js",
            "features/compensation.js",
            "intelligence/brand-packs.js",
            "intelligence/intelligence.js",
            "passkeys/passkeys.js"
        };

        AssertNoNewViolations(
            new Regex(@"(?<![\.\w])confirm\s*\(|window\.confirm\s*\(", RegexOptions.Compiled),
            allowed,
            "Use ui/confirm.js instead of native confirm().");
    }

    [Fact]
    public void NoNewGlobalDomPatchObservers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // Legacy patch-layer migration allow-list. This list may only shrink.
            "features/compensation-nav.js",

            // Shared infrastructure observers are explicitly reviewed and scoped.
            "ui/accessibility-release.js",
            "ui/appearance.js",
            "ui/motion.js"
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
    public void SettingsWorkflowsStayOutOfAppBootstrap()
    {
        var app = File.ReadAllText(Path.Combine(WwwRoot(), "app.js"));
        var settings = File.ReadAllText(Path.Combine(WwwRoot(), "features", "settings.js"));

        Assert.DoesNotContain("openDeleteAccountDialog", app);
        Assert.DoesNotContain("openTwoFactorDialog", app);
        Assert.DoesNotContain("renderSharing(", app);
        Assert.Contains("bindSettings(ctx)", app);
        Assert.Contains("renderSettings(ctx", app);

        Assert.Contains("openDeleteAccountDialog", settings);
        Assert.Contains("openTwoFactorDialog", settings);
        Assert.Contains("renderSharing(ctx)", settings);
    }

    [Fact]
    public void BootstrapLivesInApp_NotInFeatureOwners()
    {
        var app = File.ReadAllText(Path.Combine(WwwRoot(), "app.js"));
        var accounts = File.ReadAllText(Path.Combine(WwwRoot(), "features", "accounts.js"));

        Assert.Contains("initResizableSidebar();", app);
        Assert.Contains("syncResponsiveSidebar();", app);
        Assert.Contains("boot();", app);

        Assert.DoesNotContain("initResizableSidebar();", accounts);
        Assert.DoesNotContain("syncResponsiveSidebar();", accounts);
        Assert.DoesNotContain("boot();", accounts);
    }

    [Fact]
    public void AccountsIntegrationUsesSharedCoreWithoutPatchObserverOrSyntheticNavigation()
    {
        var accounts = File.ReadAllText(Path.Combine(WwwRoot(), "features", "accounts-ux.js"));
        Assert.DoesNotContain("/bff/", accounts);
        Assert.DoesNotContain("new MutationObserver", accounts);
        Assert.DoesNotContain(".click()", accounts);
        Assert.DoesNotContain("fwNavScope", accounts);
        Assert.Contains("apiClient.backend", accounts);
        Assert.Contains("navigate(", accounts);
        Assert.Contains("onAppEvent(", accounts);
    }

    [Fact]
    public void NoGlobalFeatureNavigationBridgeReturns()
    {
        var app = File.ReadAllText(Path.Combine(WwwRoot(), "app.js"));
        Assert.DoesNotContain("window.fwNavScope", app);
        Assert.DoesNotContain("window.fwOpenBudget", app);
        Assert.DoesNotContain("window.fwSyncResponsiveSidebar", app);
        Assert.DoesNotContain("window.fwClampSidebarWidth", app);
        Assert.Contains("installNavigation", app);
    }
}
