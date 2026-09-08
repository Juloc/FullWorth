using System.IO;
using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// A control must never be hidden with opacity:0 and revealed only on :hover. A phone has no hover, so
/// such a control is permanently invisible with nothing hinting that the row is actionable — which is
/// exactly how the category row's edit and archive buttons became unreachable in the PWA.
///
/// Revealing on hover is fine when it is gated on a pointer that can actually hover
/// (@media(hover:hover)), or when the resting state is merely dimmed rather than invisible.
/// </summary>
public sealed class TouchRevealGuardTests
{
    // ".cat-row:hover .cat-actions{opacity:1}" -> captures ".cat-actions"
    private static readonly Regex HoverReveal = new(
        @"([.#][A-Za-z0-9_-]+)\s*\{[^{}]*opacity\s*:\s*1\b",
        RegexOptions.Compiled);

    [Fact]
    public void NoControlIsHiddenAtRestAndRevealedOnlyOnHover()
    {
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(WwwRoot(), "*.css", SearchOption.AllDirectories))
        {
            var css = StripHoverCapableBlocks(File.ReadAllText(path));

            foreach (var rule in Rules(css))
            {
                if (!rule.Selector.Contains(":hover", StringComparison.Ordinal)) continue;

                var revealed = HoverReveal.Match(rule.Selector + "{" + rule.Body);
                if (!revealed.Success) continue;

                var target = revealed.Groups[1].Value;
                // Is that same target invisible at rest anywhere in this file?
                var hiddenAtRest = new Regex(
                    Regex.Escape(target) + @"\s*(,[^{}]*)?\{[^{}]*opacity\s*:\s*0\s*[;}]",
                    RegexOptions.Compiled);

                if (hiddenAtRest.IsMatch(css))
                    offenders.Add($"{Path.GetFileName(path)}: {target} (via '{rule.Selector.Trim()}')");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These controls rest at opacity:0 and are only revealed on :hover, so they are invisible on a " +
            "touch device: " + string.Join("; ", offenders) +
            ". Either wrap the hiding rule in @media(hover:hover) and (pointer:fine), or give the resting " +
            "state a visible opacity.");
    }

    /// <summary>
    /// Removes @media blocks whose condition requires a hover-capable pointer — inside those, hiding a
    /// control at rest is exactly the correct behaviour and must not be reported.
    /// </summary>
    private static string StripHoverCapableBlocks(string css)
    {
        var result = new System.Text.StringBuilder(css.Length);
        var index = 0;

        while (index < css.Length)
        {
            var at = css.IndexOf("@media", index, StringComparison.Ordinal);
            if (at < 0)
            {
                result.Append(css, index, css.Length - index);
                break;
            }

            var open = css.IndexOf('{', at);
            if (open < 0)
            {
                result.Append(css, index, css.Length - index);
                break;
            }

            var condition = css[at..open];
            var close = MatchingBrace(css, open);
            if (close < 0)
            {
                result.Append(css, index, css.Length - index);
                break;
            }

            result.Append(css, index, at - index);
            if (!condition.Contains("hover:hover", StringComparison.Ordinal))
                result.Append(css, open + 1, close - open - 1); // keep the inner rules, drop the wrapper

            index = close + 1;
        }

        return result.ToString();
    }

    private static int MatchingBrace(string css, int open)
    {
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static IEnumerable<(string Selector, string Body)> Rules(string css)
    {
        var index = 0;
        while (index < css.Length)
        {
            var open = css.IndexOf('{', index);
            if (open < 0) break;
            var close = css.IndexOf('}', open);
            if (close < 0) break;

            var selectorStart = css.LastIndexOf('}', open - 1 < 0 ? 0 : open - 1) + 1;
            yield return (css[selectorStart..open], css[(open + 1)..close]);
            index = close + 1;
        }
    }

    private static string WwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "FullWorth.Web", "wwwroot");
    }
}
