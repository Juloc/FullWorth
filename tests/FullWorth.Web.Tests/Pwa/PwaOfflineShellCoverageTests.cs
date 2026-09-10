using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Pwa;

/// <summary>
/// The existing PWA tests check that everything the service worker lists exists on disk. Nothing checked
/// the other direction — that everything the app shell needs is listed — and 52 of the 137 stylesheets
/// and modules in wwwroot were not, including ten `ui/` modules the shell itself imports.
///
/// The fetch handler is network-first with a cache fallback, so online this is invisible: an asset is
/// cached the first time it is fetched. Offline it is not. An installed PWA that cold-starts without a
/// connection can only work if every module reachable from index.html is already in the precache, and
/// this test walks the real import graph to insist on it.
///
/// Feature pages that are loaded on demand are deliberately NOT required here — they are not needed for
/// the shell to come up, and precaching every screen would trade offline breadth for install weight.
/// </summary>
public sealed class PwaOfflineShellCoverageTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public PwaOfflineShellCoverageTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Every_module_the_shell_imports_is_precached()
    {
        var precached = PrecachedPaths();
        var reachable = ReachableFromIndex();

        var missing = reachable.Where(path => !precached.Contains(path)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            "the app shell imports these but the service worker does not precache them, so an offline "
            + "cold start cannot load them:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Every_stylesheet_the_shell_links_is_precached()
    {
        var precached = PrecachedPaths();
        var index = File.ReadAllText(AssetPath("index.html"));

        var missing = Regex.Matches(index, """<link[^>]+rel="stylesheet"[^>]+href="(?<path>/[^"?#]+)""")
            .Select(match => match.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(path => !precached.Contains(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "index.html links these stylesheets but they are not precached:" + Environment.NewLine
            + string.Join(Environment.NewLine, missing));
    }

    private HashSet<string> PrecachedPaths()
    {
        var sw = File.ReadAllText(AssetPath("sw.js"));
        var shell = sw[(sw.IndexOf("const APP_SHELL = [", StringComparison.Ordinal) + 1)..];
        shell = shell[..shell.IndexOf("];", StringComparison.Ordinal)];
        return Regex.Matches(shell, """['"](?<path>/[^'"]+)['"]""")
            .Select(match => match.Groups["path"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Walks the real import graph from the modules index.html loads. Both static and dynamic import
    /// specifiers count: a dynamically imported module is still needed the moment that code path runs.
    /// </summary>
    private HashSet<string> ReachableFromIndex()
    {
        var index = File.ReadAllText(AssetPath("index.html"));
        var queue = new Queue<string>(Regex
            .Matches(index, """<script[^>]+type="module"[^>]+src="(?<path>/[^"?#]+)""")
            .Select(match => match.Groups["path"].Value));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            if (!seen.Add(path)) continue;
            var file = AssetPath(path.TrimStart('/'));
            if (!File.Exists(file)) continue;
            foreach (var specifier in Regex
                         .Matches(File.ReadAllText(file), """(?:from|import)\s*\(?\s*['"](?<spec>\.{1,2}/[^'"]+)['"]""")
                         .Select(match => match.Groups["spec"].Value))
            {
                var resolved = Resolve(path, specifier);
                if (resolved is not null) queue.Enqueue(resolved);
            }
        }

        return seen;
    }

    /// <summary>Resolves a relative specifier against the importing module's directory.</summary>
    private static string? Resolve(string importer, string specifier)
    {
        var segments = new List<string>(importer.TrimStart('/').Split('/'));
        segments.RemoveAt(segments.Count - 1);
        foreach (var segment in specifier.Split('/'))
        {
            if (segment is "." or "") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) return null;
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return '/' + string.Join('/', segments);
    }

    private string AssetPath(string relative) =>
        Path.Combine(
            factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath,
            relative.Replace('/', Path.DirectorySeparatorChar));
}
