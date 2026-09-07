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
            "features/receipt-scan-ai.js",
            "features/receipt-scan-set.js",
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
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // Transitional shell/client.
            // Legacy migration allow-list. This list may only shrink.
            "features/purchase-articles-advanced-actions.js",
            "features/purchase-receipt-source-review.js",
            "features/receipt-scan-ai.js",
            "features/transactions.js",
        };

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
            "features/capability-ui-guard.js"
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
            "features/purchase-articles-advanced-actions.js",
            "features/purchase-discount-actions.js",
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
            // Accounts is explicitly frozen until its separate redesign is approved.
            "features/accounts-ux.js",

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
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "purchase-articles-advanced-installer.js"
        };

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
    public void AccountsCleanupRemainsBlockedUntilExplicitMigration()
    {
        var plan = File.ReadAllText(Path.Combine(Root(), "docs", "FRONTEND_ARCHITECTURE_CLEANUP_PLAN.md"));
        Assert.Contains("Accounts migration — BLOCKED", plan);
        Assert.True(File.Exists(Path.Combine(WwwRoot(), "features", "accounts-ux.js")));
    }
}
