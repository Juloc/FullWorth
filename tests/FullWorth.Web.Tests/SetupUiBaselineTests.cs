using System.Text.Json;

namespace FullWorth.Web.Tests;

/// <summary>
/// What a self-hoster meets in the first five minutes.
///
/// Two things went wrong there and both were about what the screen SAYS, not about what the code does.
/// A brand-new installation showed a plain sign-in form, so the way in — "register" — was a link in
/// the corner. And picking Codex in the setup assistant answered with the bare token
/// <c>codex_bridge_unavailable</c>: a machine code, for a container that is off by default, with no
/// hint that it is off or how to turn it on.
/// </summary>
public sealed class SetupUiBaselineTests
{
    /// <summary>
    /// The sign-in view is the DEFAULT, not a choice. Visiting "/" redirects to
    /// <c>/auth/login?returnUrl=%2F</c>, so a first-run redirect that only fires on "no view named"
    /// never fires at all — which is how the first attempt at this silently did nothing.
    /// </summary>
    [Fact]
    public void First_run_lands_on_setup_even_though_the_redirect_names_the_login_view()
    {
        var auth = ReadSource(Path.Combine("auth", "auth.js"));

        Assert.Contains("firstRun: payload?.firstRun === true", auth, StringComparison.Ordinal);
        Assert.Contains(
            "requested === null || requested === 'login'",
            auth,
            StringComparison.Ordinal);
        // Re-pointed translation keys, not text written once: the language toggle re-renders and
        // would otherwise put "Registrieren" straight back.
        Assert.Contains("auth.pages.firstRun.title", auth, StringComparison.Ordinal);
        Assert.Contains("dataset.i18n = 'auth.pages.firstRun.subtitle'", auth, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_locales_carry_the_first_run_wording()
    {
        foreach (var locale in new[] { "de", "en" })
        {
            using var json = JsonDocument.Parse(ReadSource(Path.Combine("locales", locale + ".json")));
            var firstRun = json.RootElement.GetProperty("auth").GetProperty("pages").GetProperty("firstRun");

            Assert.False(string.IsNullOrWhiteSpace(firstRun.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(firstRun.GetProperty("subtitle").GetString()));
        }
    }

    /// <summary>
    /// The Codex container is profile-gated and off in every default stack, so this error is the
    /// NORMAL answer there, not an exception. It has to read like a sentence with a next step.
    /// </summary>
    [Fact]
    public void A_codex_error_code_never_reaches_the_screen_as_a_token()
    {
        var setup = ReadSource(Path.Combine("features", "access-setup.js"));

        Assert.Contains("codex_bridge_unavailable: 'aiAccess.codexNotDeployed'", setup, StringComparison.Ordinal);
        Assert.Contains("codexErrorText(error)", setup, StringComparison.Ordinal);
        // The raw message must not be what the Codex step prints on failure any more.
        Assert.DoesNotContain(
            "error.message || get('aiAccess.codexUnavailable')",
            setup.Replace("return error?.message || get('aiAccess.codexUnavailable');", string.Empty),
            StringComparison.Ordinal);

        foreach (var locale in new[] { "de", "en" })
        {
            using var json = JsonDocument.Parse(ReadSource(Path.Combine("locales", locale + ".json")));
            var ai = json.RootElement.GetProperty("aiAccess");
            var text = ai.GetProperty("codexNotDeployed").GetString();

            Assert.False(string.IsNullOrWhiteSpace(text));
            // It has to name the way out, not merely state the problem.
            Assert.Contains("--profile codex", text!, StringComparison.Ordinal);
        }
    }


    /// <summary>
    /// Every password field gets the show/hide eye, and none of them carries its own copy of it.
    ///
    /// It existed exactly once - hand-written into the sign-in markup as a wrapper, two inline SVGs
    /// and four ARIA attributes - so the other fifteen password inputs in this app had none,
    /// registration included. The answer is not fifteen more copies: ui/password-toggle.js upgrades
    /// an ordinary <input type="password"> in place, so a template string stays a template string.
    /// </summary>
    [Fact]
    public void The_password_eye_lives_in_one_module_and_not_in_the_markup()
    {
        var auth = ReadSource(Path.Combine("auth", "index.html"));
        var module = ReadSource(Path.Combine("ui", "password-toggle.js"));
        var formDialog = ReadSource(Path.Combine("ui", "form-dialog.js"));

        // The markup carries plain inputs again - no wrapper, no button, no inline eye.
        Assert.DoesNotContain("password-toggle", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("pw-eye", auth, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", auth, StringComparison.Ordinal);

        // The module owns the button, and the auth page has no imports of the app bundle to rely on.
        Assert.Contains("export function enhancePasswordInputs", module, StringComparison.Ordinal);
        Assert.DoesNotContain("import ", module, StringComparison.Ordinal);

        // And a dialog field of kind Password gets it without the caller doing anything.
        Assert.Contains("Password: 'password'", formDialog, StringComparison.Ordinal);
        Assert.Contains("enhancePasswordInputs(form, passwordLabels)", formDialog, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FullWorth.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(
            Path.Combine(directory!.FullName, "src", "FullWorth.Web", "wwwroot", relativePath));
    }
}
