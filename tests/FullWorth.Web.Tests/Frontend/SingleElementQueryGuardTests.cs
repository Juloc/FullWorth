using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// Every frontend module defines its own two-character query helpers: <c>$</c> returns ONE element
/// (querySelector or getElementById), <c>$$</c> returns a list. Iterating the result of <c>$</c> therefore
/// always throws — and it throws on the FIRST element, so everything after it in that function silently
/// never happens.
///
/// This is not hypothetical: a refactor turned three <c>$$(</c> into <c>$(</c> in
/// <c>features/accounts-presentation.js</c>, which killed the whole accounts-page decoration (identity
/// icons, unread dots, performance badges, group icons, bank logos) for two days. There are no browser
/// tests in this repo, so nothing noticed. One character, invisible in review, and a text search for
/// <c>$$</c> does not find its absence — hence a guard.
/// </summary>
public sealed class SingleElementQueryGuardTests
{
    [Fact]
    public void NoModuleIteratesTheResultOfTheSingleElementQueryHelper()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(WwwRoot(), "*.js", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            // Only files that actually define $ as a single-element lookup; a module is free to name
            // something else $.
            if (!source.Contains("$=(s,r=document)=>r.querySelector(s)") &&
                !source.Contains("$=s=>document.querySelector(s)") &&
                !source.Contains("$ = s => document.querySelector(s)") &&
                !source.Contains("$=id=>document.getElementById(id)") &&
                !source.Contains("$ = id => document.getElementById(id)"))
                continue;

            foreach (var index in Occurrences(source, "of $("))
            {
                // `for (const row of $('sel').querySelectorAll(...))` is fine: what gets iterated is the
                // list the method returned, not the element. Only a directly iterated $() is a bug.
                var afterCall = source[EndOfCall(source, index + "of ".Length)..];
                if (afterCall.StartsWith('.')) continue;
                offenders.Add($"{Path.GetRelativePath(WwwRoot(), file)}: {Snippet(source, index)}");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>Every start index of <paramref name="needle"/>, allowing an optional space after `of`.</summary>
    private static IEnumerable<int> Occurrences(string source, string needle)
    {
        foreach (var candidate in new[] { needle, needle.Replace("of $", "of$") })
        {
            var index = source.IndexOf(candidate, System.StringComparison.Ordinal);
            while (index >= 0)
            {
                yield return index;
                index = source.IndexOf(candidate, index + 1, System.StringComparison.Ordinal);
            }
        }
    }

    /// <summary>Index just past the <c>)</c> that closes the call starting at <paramref name="start"/>.</summary>
    private static int EndOfCall(string source, int start)
    {
        var depth = 0;
        for (var index = source.IndexOf('(', start); index >= 0 && index < source.Length; index++)
        {
            if (source[index] == '(') depth++;
            else if (source[index] == ')' && --depth == 0) return index + 1;
        }
        return source.Length;
    }

    private static string Snippet(string source, int index)
    {
        var from = System.Math.Max(0, index - 40);
        var length = System.Math.Min(120, source.Length - from);
        return source.Substring(from, length).Replace('\n', ' ').Replace('\r', ' ');
    }

    private static string WwwRoot() =>
        Path.Combine(Root(), "src", "FullWorth.Web", "wwwroot");

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
