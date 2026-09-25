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
            // It has to name the way out, not merely state the problem. It used to say "start the
            // second container with --profile codex"; there is no second container any more, and a
            // message telling an operator to start one would send them looking for something that
            // does not exist. What it can honestly say is: it starts itself, wait, then read the log.
            Assert.DoesNotContain("--profile codex", text!, StringComparison.Ordinal);
            Assert.Contains("fullworth-codex", text!, StringComparison.Ordinal);
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
        var module = ReadSource(Path.Combine("components", "password-toggle.js"));
        var formDialog = ReadSource(Path.Combine("components", "form-dialog.js"));

        // The markup carries plain inputs again - no wrapper, no button, no inline eye.
        Assert.DoesNotContain("password-toggle", auth, StringComparison.Ordinal);
        Assert.DoesNotContain("pw-eye", auth, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", auth, StringComparison.Ordinal);

        // The module owns the button, and the auth page has no app bundle to rely on. Its one import is
        // the sprite reference (#154), a leaf that imports nothing and reads the address the auth page
        // puts on its <body>.
        Assert.Contains("export function enhancePasswordInputs", module, StringComparison.Ordinal);
        Assert.Equal(["import { spriteHref } from './sprite.js';"],
            module.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("import ", StringComparison.Ordinal)));
        Assert.DoesNotContain("import ", ReadSource(Path.Combine("components", "sprite.js")), StringComparison.Ordinal);
        Assert.Contains("data-sprite=\"/icons/sprite.svg\"", auth, StringComparison.Ordinal);

        // And a dialog field of kind Password gets it without the caller doing anything.
        Assert.Contains("Password: 'password'", formDialog, StringComparison.Ordinal);
        Assert.Contains("enhancePasswordInputs(form, passwordLabels)", formDialog, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Assistent fragte nie, in welcher Sprache die Standardkategorien heissen sollen (#117) -
    /// und es gab auch nur Englisch. Jetzt ist es der erste Schritt, weil er als einziger Daten
    /// anfasst; die drei anderen richten nur Zugaenge ein.
    ///
    /// Die Frage darf folgenlos bleiben, wenn jemand schon eine Standardkategorie umbenannt hat: eine
    /// Umbenennung ist eine Entscheidung, und eine Spracheinstellung ueberschreibt sie nicht. Dann
    /// steht der Grund da, statt dass ein Klick still nichts tut.
    /// </summary>
    [Fact]
    public void TheSetupWizardAsksForTheCategoryLanguageFirst()
    {
        var wizard = ReadSource(Path.Combine("features", "access-setup.js"));
        var de = JsonDocument.Parse(ReadSource(Path.Combine("locales", "de.json")));
        var en = JsonDocument.Parse(ReadSource(Path.Combine("locales", "en.json")));

        Assert.Contains("api/categories/language", wizard);
        Assert.Contains("categoryStep", wizard);
        Assert.Contains("[data-start]').onclick = categoryStep", wizard);
        // Die Zaehlung ist seit components/wizard.js kein literaler String im Schritt-Markup mehr,
        // sondern ein {step, total}-Paar, das der gemeinsame Baustein selbst zu "N / M" rendert.
        Assert.Contains("{ step: 1, total: 5 }", wizard);
        Assert.Contains("{ step: 2, total: 5 }", wizard);
        Assert.Contains("{ step: 3, total: 5 }", wizard);
        Assert.DoesNotContain("setup-progress", wizard);

        // Gesperrt heisst gesperrt: keine Auswahl, keine Anfrage, aber eine Begruendung.
        Assert.Contains("state?.canChange", wizard);
        Assert.Contains("onboarding.categoriesLocked", wizard);
        Assert.Contains("if (!state?.canChange || picked === current)", wizard);

        foreach (var key in new[]
                 { "categoriesTitle", "categoriesText", "categoriesGerman", "categoriesEnglish", "categoriesLocked" })
        {
            Assert.True(de.RootElement.GetProperty("onboarding").TryGetProperty(key, out _), $"de.json: onboarding.{key}");
            Assert.True(en.RootElement.GetProperty("onboarding").TryGetProperty(key, out _), $"en.json: onboarding.{key}");
        }
    }

    /// <summary>
    /// Kursdaten sind der neue vierte Schritt zwischen Bank- und Cloud-Zugang (5 statt 4 insgesamt).
    /// GET /auth/admin/instance-settings ist admin-only, laeuft aber fuer jede neu registrierte
    /// Person - eine Nicht-Admin-Person bekommt 403 und darf nie einen leeren/kaputten Schritt sehen,
    /// sondern muss direkt bei cloudStep landen, exakt wie categoryStep und cloudStep das schon fuer
    /// ihre eigenen admin-only Aufrufe machen.
    /// </summary>
    [Fact]
    public void TheSetupWizardOffersAMarketDataStepBetweenBankAndCloud()
    {
        var wizard = ReadSource(Path.Combine("features", "access-setup.js"));
        var de = JsonDocument.Parse(ReadSource(Path.Combine("locales", "de.json")));
        var en = JsonDocument.Parse(ReadSource(Path.Combine("locales", "en.json")));

        Assert.Contains("marketDataStep", wizard);
        Assert.Contains("/auth/admin/instance-settings", wizard);
        Assert.Contains("MarketData:Provider", wizard);
        // Wie oben: das Zaehl-Paar statt eines literalen "N / 5"-Strings.
        Assert.Contains("{ step: 4, total: 5 }", wizard);
        Assert.Contains("{ step: 5, total: 5 }", wizard);

        // bankStep fuehrt jetzt in den neuen Schritt, der neue Schritt in cloudStep - nicht mehr direkt
        // von bankStep zu cloudStep.
        Assert.Contains("step.querySelector('[data-finish]').onclick = marketDataStep;", wizard);
        Assert.Contains("step.querySelector('[data-back]').onclick = marketDataStep;", wizard);

        // 403 (oder jeder andere Fehler) auf GET ueberspringt den Schritt still, statt ihn leer zu zeigen.
        Assert.Contains(
            "try { settings = await instanceSettingsApi('/auth/admin/instance-settings'); }\n" +
            "      catch { await cloudStep(); return; }",
            wizard.Replace("\r\n", "\n"));

        // Gespeichert wird nur bei einer Aenderung, per PUT - keine eigene Vorlage in diesem Dialog.
        Assert.Contains("jsonBody({ key: 'MarketData:Provider', value: picked }, 'PUT')", wizard);
        Assert.Contains("if (picked === current) return cloudStep();", wizard);

        foreach (var key in new[]
                 {
                     "marketDataTitle", "marketDataText", "marketDataNone",
                     "marketDataYahoo", "marketDataYahooHint", "marketDataCustom", "marketDataCustomHint"
                 })
        {
            Assert.True(de.RootElement.GetProperty("onboarding").TryGetProperty(key, out _), $"de.json: onboarding.{key}");
            Assert.True(en.RootElement.GetProperty("onboarding").TryGetProperty(key, out _), $"en.json: onboarding.{key}");
        }
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
