using System.Text.RegularExpressions;

namespace FullWorth.Web.Tests;

/// <summary>
/// Step 6 of the dialog plan in docs/UI_AUDIT.md: the guard, because without one this grows straight
/// back. The census went from 73 to 76 dialog call sites during the audit itself.
///
/// Two rules, both with a baseline. A baseline list is the point: demanding that every remaining
/// offender be converted before the guard can exist is how a guard never gets written. Each list may
/// shrink and may never grow — converting a dialog means deleting its line.
/// </summary>
public sealed class DialogComplexityGuardTests
{
    /// <summary>
    /// Above this many controls in one flat dialog, a person is reading a form rather than answering a
    /// question. Six is what the converted editors landed on (5–8 visible, the rest behind a
    /// <c>&lt;details&gt;</c>), so it is the measured line, not a round number.
    /// </summary>
    private const int MaxFlatControls = 6;

    /// <summary>
    /// Dialogs that still put more than <see cref="MaxFlatControls"/> controls in one flat list. Every
    /// one of them is a conversion waiting to happen; the count is what the file currently has, so a
    /// change that makes one of them worse also fails this test.
    /// </summary>
    private static readonly Dictionary<string, int> KnownFlatDialogs = new()
    {
        ["features/budgets.js"] = 8,
        ["features/contracts.js"] = 11,
        ["features/transactions.js"] = 7,
        ["features/wealth-real-estate-advanced.js"] = 8
    };

    /// <summary>
    /// What is left, and why each one is left.
    ///
    /// A colour written as <c>var(--token, #fallback)</c> does not count: the token exists, so the hex
    /// never renders. The audit's "~220 hex literals" counted those. The real number outside
    /// <c>tokens.css</c> was 62, of which 28 sit in the standalone <c>account-deletion</c> page — it
    /// links no <c>tokens.css</c> at all, so a local palette is correct there and it is exempt. Of the
    /// remaining 34, 22 are now tokens. These eleven stay:
    ///
    /// <list type="bullet">
    ///   <item><c>app.css</c>: the toggle knob is a constant white circle on a coloured track. It is
    ///         not a surface, and theming it would make it vanish on one of the two tracks.</item>
    ///   <item><c>purchase-articles-workspace.css</c>: the barcode scanner's video letterbox is black
    ///         because a camera frame is letterboxed against black, not against a page surface.</item>
    ///   <item><c>auth.css</c> / <c>passkeys.css</c>: the sign-in pages' brand gradients. They are
    ///         deliberately outside the theme — the mark and the primary button look the same whichever
    ///         theme the browser asks for, which is what a sign-in page wants.</item>
    /// </list>
    /// </summary>
    private static readonly Dictionary<string, int> KnownRawColours = new()
    {
        ["app.css"] = 1,
        ["styles/features/purchase-articles-workspace.css"] = 1,
        ["auth/auth.css"] = 5,
        ["passkeys/passkeys.css"] = 4
    };

    /// <summary>Standalone pages that do not link tokens.css, so a local palette is correct there.</summary>
    private static readonly string[] SelfContainedPages = ["account-deletion"];

    [Fact]
    public void No_new_dialog_puts_more_than_six_controls_in_one_flat_list()
    {
        var offenders = new Dictionary<string, int>();

        foreach (var file in JavaScriptFiles())
        {
            var relative = Relative(file);
            var source = File.ReadAllText(file);
            foreach (var markup in DialogMarkup(source))
            {
                var controls = Regex.Matches(markup, "<input|<select|<textarea").Count;
                if (controls <= MaxFlatControls) continue;
                // A disclosure is exactly the fix, so a dialog that has one is not flat.
                if (markup.Contains("<details", StringComparison.Ordinal)) continue;
                offenders[relative] = Math.Max(offenders.GetValueOrDefault(relative), controls);
            }
        }

        var appeared = offenders.Keys.Where(x => !KnownFlatDialogs.ContainsKey(x)).OrderBy(x => x).ToList();
        Assert.True(appeared.Count == 0,
            "New flat dialogs (convert them with ui/form-dialog.js, or say why here): "
            + string.Join(", ", appeared.Select(x => $"{x} ({offenders[x]} controls)")));

        var worse = offenders
            .Where(x => KnownFlatDialogs.TryGetValue(x.Key, out var known) && x.Value > known)
            .Select(x => $"{x.Key}: {KnownFlatDialogs[x.Key]} -> {x.Value}")
            .OrderBy(x => x)
            .ToList();
        Assert.True(worse.Count == 0, "Dialogs that got more crowded: " + string.Join(", ", worse));
    }

    /// <summary>
    /// The list may only shrink. Without this half, converting a dialog leaves a stale entry that
    /// quietly raises the ceiling for the next one.
    /// </summary>
    [Fact]
    public void The_flat_dialog_baseline_has_no_stale_entries()
    {
        var current = new Dictionary<string, int>();
        foreach (var file in JavaScriptFiles())
        {
            var relative = Relative(file);
            foreach (var markup in DialogMarkup(File.ReadAllText(file)))
            {
                var controls = Regex.Matches(markup, "<input|<select|<textarea").Count;
                if (controls <= MaxFlatControls || markup.Contains("<details", StringComparison.Ordinal)) continue;
                current[relative] = Math.Max(current.GetValueOrDefault(relative), controls);
            }
        }

        var stale = KnownFlatDialogs.Keys
            .Where(known => !current.ContainsKey(known) || current[known] < KnownFlatDialogs[known])
            .OrderBy(x => x)
            .ToList();
        Assert.True(stale.Count == 0,
            "These improved — lower or delete their entry in KnownFlatDialogs: " + string.Join(", ", stale));
    }

    [Fact]
    public void No_new_stylesheet_hardcodes_a_colour()
    {
        var offenders = new Dictionary<string, int>();

        foreach (var file in StyleSheets())
        {
            var relative = Relative(file);
            if (relative.EndsWith("tokens.css", StringComparison.Ordinal)) continue;
            if (SelfContainedPages.Any(page => relative.StartsWith(page + "/", StringComparison.Ordinal))) continue;

            var css = File.ReadAllText(file);
            // A hex inside a var() fallback is unreachable while the token exists, so it is noise, not a
            // hardcoded colour. Strip those before counting.
            var withoutFallbacks = Regex.Replace(css, @"var\(--[a-z0-9-]+\s*,[^)]*\)", "VAR");
            var count = Regex.Matches(withoutFallbacks, "#[0-9a-fA-F]{3,8}\\b").Count;
            if (count > 0) offenders[relative] = count;
        }

        var appeared = offenders.Keys.Where(x => !KnownRawColours.ContainsKey(x)).OrderBy(x => x).ToList();
        Assert.True(appeared.Count == 0,
            "New hardcoded colours (use a token from styles/tokens.css): "
            + string.Join(", ", appeared.Select(x => $"{x} ({offenders[x]})")));

        var worse = offenders
            .Where(x => KnownRawColours.TryGetValue(x.Key, out var known) && x.Value > known)
            .Select(x => $"{x.Key}: {KnownRawColours[x.Key]} -> {x.Value}")
            .OrderBy(x => x)
            .ToList();
        Assert.True(worse.Count == 0, "Stylesheets that gained hardcoded colours: " + string.Join(", ", worse));
    }

    /// <summary>
    /// One dialog height, and it lives in dialogs.css. Six features used to set their own — 92vh, 90vh,
    /// 86vh, 80vh, min(88vh,920px), min(900px,100dvh-24px) — so every dialog stopped somewhere else.
    /// A feature that needs an inner scroll region says <c>overflow:auto</c> on that region.
    /// </summary>
    [Fact]
    public void Only_the_shared_layer_sets_a_dialog_height()
    {
        var offenders = new List<string>();

        foreach (var file in StyleSheets())
        {
            var relative = Relative(file);
            if (relative.EndsWith("dialogs.css", StringComparison.Ordinal)) continue;

            foreach (var declaration in Regex.Matches(File.ReadAllText(file), @"[^;{}]*max-height:[^;{}]+").Select(m => m.Value))
            {
                // Only care about a whole dialog or its card, not a list inside one.
                if (!Regex.IsMatch(declaration, @"dialog|-card|-dlg", RegexOptions.IgnoreCase)) continue;
                // `none` and `inherit` hand the decision back to the shared layer, which is the point.
                if (declaration.Contains("max-height:none", StringComparison.Ordinal)
                    || declaration.Contains("max-height:inherit", StringComparison.Ordinal)) continue;
                // A bottom sheet is anchored to the bottom edge, so it owns its own ceiling.
                if (declaration.Contains("sortsheet", StringComparison.Ordinal)
                    || declaration.Contains("more-sheet", StringComparison.Ordinal)
                    || declaration.Contains("receipt-set-card", StringComparison.Ordinal)) continue;
                // The drawer is full height by definition and carries the vh/dvh fallback pair.
                if (declaration.Contains("dialog.drawer", StringComparison.Ordinal)) continue;
                offenders.Add($"{relative}: {declaration.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A dialog height outside dialogs.css: " + string.Join(" | ", offenders));
    }

    /// <summary>
    /// <c>vh</c> is the LARGEST viewport, so a 92vh dialog on a phone reaches under the browser chrome.
    /// Every dialog height is <c>dvh</c>, except where a <c>vh</c> line is immediately followed by the
    /// <c>dvh</c> one as a progressive-enhancement pair.
    /// </summary>
    [Fact]
    public void A_dialog_height_is_measured_in_dvh()
    {
        var offenders = new List<string>();

        foreach (var file in StyleSheets())
        {
            var css = File.ReadAllText(file);
            foreach (var declaration in Regex.Matches(css, @"[^;{}]*max-height:[^;{}]*vh[^;{}]*").Select(m => m.Value))
            {
                if (declaration.Contains("dvh", StringComparison.Ordinal)) continue;
                // The pair `max-height:100vh;max-height:100dvh` is the supported-browser fallback.
                var index = css.IndexOf(declaration, StringComparison.Ordinal);
                var after = css.Substring(index + declaration.Length, Math.Min(40, css.Length - index - declaration.Length));
                if (after.Contains("dvh", StringComparison.Ordinal)) continue;
                offenders.Add($"{Relative(file)}: {declaration.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "vh where dvh belongs: " + string.Join(" | ", offenders));
    }

    /// <summary>
    /// A custom property that is declared nowhere is the quietest styling bug there is: with a fallback
    /// the fallback renders (so a colour is off-palette but plausible), and without one the whole
    /// declaration is invalid and silently dropped. Found twelve of them this way —
    /// <c>var(--surface-elevated, Canvas)</c> rendered the OS canvas colour, <c>var(--mono, monospace)</c>
    /// rendered the browser default instead of the app face, and <c>border-radius: var(--radius)</c>
    /// rendered no radius at all.
    /// </summary>
    [Fact]
    public void Every_custom_property_a_stylesheet_reads_is_declared_somewhere()
    {
        // Set per element by JavaScript, so they are correctly absent from every stylesheet.
        string[] inlineOnly = ["--depth", "--ident-h"];

        var declared = new HashSet<string>(StringComparer.Ordinal);
        var sheets = StyleSheets().ToList();
        foreach (var file in sheets)
            foreach (var match in Regex.Matches(File.ReadAllText(file), @"(--[a-z0-9-]+)\s*:").Cast<Match>())
                declared.Add(match.Groups[1].Value);

        var missing = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in sheets)
            foreach (var match in Regex.Matches(File.ReadAllText(file), @"var\((--[a-z0-9-]+)").Cast<Match>())
            {
                var name = match.Groups[1].Value;
                if (declared.Contains(name) || inlineOnly.Contains(name)) continue;
                missing.TryAdd(name, Relative(file));
            }

        Assert.True(missing.Count == 0,
            "Custom properties read but never declared: "
            + string.Join(", ", missing.Select(x => $"{x.Key} (in {x.Value})")));
    }

    // ---- the census itself ----

    /// <summary>
    /// The markup of every template-literal dialog call. Deliberately simple: the call sites in this
    /// codebase are all `ctx.dialog(`...`)` or `createDialog(`...`)` with the markup inline, which is
    /// exactly what the audit counted.
    /// </summary>
    private static IEnumerable<string> DialogMarkup(string source)
    {
        foreach (var match in Regex.Matches(source, @"(?:ctx\.dialog|createDialog)\(`").Cast<Match>())
        {
            var start = match.Index + match.Length;
            var end = source.IndexOf("`)", start, StringComparison.Ordinal);
            if (end < 0) continue;
            yield return source[start..end];
        }
    }

    private static IEnumerable<string> JavaScriptFiles() =>
        Directory.EnumerateFiles(WwwRoot(), "*.js", SearchOption.AllDirectories)
            .Where(x => !x.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static IEnumerable<string> StyleSheets() =>
        Directory.EnumerateFiles(WwwRoot(), "*.css", SearchOption.AllDirectories);

    private static string Relative(string absolute) =>
        Path.GetRelativePath(WwwRoot(), absolute).Replace(Path.DirectorySeparatorChar, '/');

    private static string WwwRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "FullWorth.Web", "wwwroot");
    }
}
