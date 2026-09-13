using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Frontend;

/// <summary>
/// A message raised from inside a dialog has to be on screen.
///
/// It was not. A <c>&lt;dialog&gt;</c> opened with <c>showModal()</c> renders in the browser's top
/// layer, which is above every z-index there is, and the toast is an ordinary positioned element with
/// <c>z-index: 90</c>. On a desktop it was painted behind the dim backdrop; on a phone, where the
/// dialog covers the whole screen, it was invisible outright — measured at 375×812 with the toast at
/// full opacity and the screen showing nothing.
///
/// Over 170 places report a failure from inside a dialog with a toast, so this was the normal way for
/// the app to tell somebody that something went wrong.
///
/// These are source assertions because this repo has no browser test. Each one corresponds to a way
/// the fix silently comes undone, and all of them were verified in a real browser first.
/// </summary>
public sealed class ToastVisibilityTests
{
    private static string Read(string relative)
    {
        using var factory = new FullWorthWebFactory();
        using var client = factory.CreateClient();
        var root = factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        return File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// The top layer is reachable in exactly one way for something that is not a modal dialog. If this
    /// disappears the toast silently returns to being painted underneath every dialog in the app.
    /// </summary>
    [Fact]
    public void The_toast_enters_the_top_layer()
    {
        var source = Read("components/toast.js");

        Assert.Contains("showPopover()", source, StringComparison.Ordinal);
        // manual, not auto: an auto popover light-dismisses on the next click anywhere, so a message
        // that appears while somebody is typing would vanish as they carry on.
        Assert.Contains("'popover', 'manual'", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The order that cost an hour: showing the popover first left the element at opacity 0 and it
    /// stayed there — open, in the top layer, reported as shown by every property a test could read,
    /// and completely invisible.
    /// </summary>
    [Fact]
    public void The_visible_class_is_set_before_the_top_layer()
    {
        var source = Read("components/toast.js");
        var show = source[source.IndexOf("function show(", StringComparison.Ordinal)..];

        var addClass = show.IndexOf("classList.add('show')", StringComparison.Ordinal);
        var enter = show.IndexOf("enterTopLayer()", StringComparison.Ordinal);

        Assert.True(addClass >= 0 && enter >= 0);
        Assert.True(
            addClass < enter,
            "enterTopLayer() runs before the class is set; the toast then stays at opacity 0.");
    }

    /// <summary>
    /// A popover is display:none while closed, so an element driven by hand — textContent plus
    /// classList.add('show') — shows nothing at all. Three files used to do exactly that, each with
    /// its own timer. They go through the shared controller now, and nothing else may reach for the
    /// element directly.
    /// </summary>
    [Fact]
    public void Nothing_drives_the_toast_element_by_hand()
    {
        using var factory = new FullWorthWebFactory();
        using var client = factory.CreateClient();
        var root = factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories))
        {
            // The controller itself is the one place allowed to touch it.
            if (Path.GetFileName(file) == "toast.js") continue;

            var code = Regex.Replace(File.ReadAllText(file), @"/\*[\s\S]*?\*/|//.*", string.Empty);
            if (Regex.IsMatch(code, @"getElementById\(\s*['""]toast['""]\s*\)")
                || Regex.IsMatch(code, @"querySelector\(\s*['""]#toast['""]\s*\)"))
                offenders.Add(Path.GetRelativePath(root, file));
        }

        Assert.True(
            offenders.Count == 0,
            "These reach for #toast directly instead of the shared controller, and a popover that is "
            + "not shown displays nothing: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Nur noch eine Zeile je Fall: Admin hatte einen zweiten Melder mit denselben Regeln, solange es
    /// ein eigenes Dokument war. Als Seite der Hülle benutzt es den einen, der schon da ist.
    ///
    /// The popover user-agent style centres the element (<c>inset: 0; margin: auto</c>) and gives it a
    /// border. Without taking that back the toast jumps into the middle of the screen the moment it
    /// works at all.
    /// </summary>
    [Theory]
    [InlineData("app.css", "#toast[popover]")]
    public void The_popover_default_styling_is_taken_back(string stylesheet, string selector)
    {
        var css = Read(stylesheet);

        var rule = css[css.IndexOf(selector, StringComparison.Ordinal)..];
        rule = rule[..rule.IndexOf('}')];

        Assert.Contains("position:fixed", rule, StringComparison.Ordinal);
        Assert.Contains("margin:0", rule, StringComparison.Ordinal);
        Assert.Contains("border:0", rule, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the phone layout keeps winning. <c>#toast[popover]</c> is an id plus an attribute, which
    /// outranks the plain <c>#toast</c> in the media query — so the full-width treatment has to name
    /// the attribute too or the toast stays pinned to the right edge on a phone.
    /// </summary>
    [Theory]
    [InlineData("styles/responsive.css", "#toast,#toast[popover]")]
    public void The_phone_layout_still_outranks_the_popover_rule(string stylesheet, string selector) =>
        Assert.Contains(selector, Read(stylesheet), StringComparison.Ordinal);
}
