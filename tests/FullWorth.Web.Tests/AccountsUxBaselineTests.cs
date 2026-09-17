using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

public sealed class AccountsUxBaselineTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory _factory;
    private readonly HttpClient _client;

    public AccountsUxBaselineTests(FullWorthWebFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public void AccountsPresentation_IsOwnedByAccountsModule_AndIncludedInPwaShell()
    {
        // Read the shipped app shell directly. The served "/" is behind RequireAuthorization, so an
        // unauthenticated test client is redirected to the auth shell instead of index.html; the static
        // index.html read here is exactly what an authenticated user receives via MapFallbackToFile.
        var html = ReadAsset("index.html");
        var sw = ReadAsset("sw.js");

        var accounts = ReadAsset("pages", "accounts", "page.js");
        Assert.DoesNotContain("/features/accounts-ux.js", html);
        Assert.Contains("/pages/accounts/page.css", html);
        Assert.Contains("from './presentation.js'", accounts);

        Assert.Contains("/pages/accounts/page.js", sw);
        Assert.Contains("/pages/accounts/presentation.js", sw);
        Assert.Contains("/pages/accounts/page.css", sw);
    }

    private string ReadAsset(params string[] path)
    {
        var environment = _factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(new[] { environment.WebRootPath }.Concat(path).ToArray()));
    }

    [Fact]
    public async Task AccountsPresentation_UsesSharedApiAndPersistentAccountGroupApis()
    {
        var js = await GetAsync("/pages/accounts/presentation.js");

        Assert.DoesNotContain("/bff/", js);
        Assert.Contains("apiClient.backend", js);
        Assert.Contains("apiClient.banking", js);
        Assert.Contains("api/accounts", js);
        Assert.Contains("api/account-groups", js);
        Assert.Contains("api/preferences/", js);
        Assert.Contains("accounts.visuals", js);
        Assert.Contains("account-groups.visuals", js);
        Assert.Contains("transactions.seenAt", js);

        Assert.DoesNotContain("http://fullworth-backend:8080", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://fullworth-banking:8080", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-FullWorth-Banking-Key", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new MutationObserver", js);
        Assert.DoesNotContain(".click()", js);
        Assert.DoesNotContain("fwNavScope", js);
        Assert.Contains("navigate(", js);
        Assert.Contains("bindAccountsPresentation", js);
        Assert.DoesNotContain("onAppEvent(", js);
    }

    [Fact]
    public async Task ConnectedAccounts_DefaultToBankLogo_AndVisualOverrideCanBeReset()
    {
        var js = await GetAsync("/pages/accounts/presentation.js");

        Assert.Contains("bankForAccount", js);
        Assert.Contains("hasVisualOverride", js);
        Assert.Contains("restoreDefault", js);
        Assert.Contains("delete S.prefs.accounts[a.id]", js);
        Assert.DoesNotContain("data-acct", js);
        Assert.Contains("bankDefault=!!a.bankConnectionId||!!lg", js);
    }

    [Fact]
    public void BankPicker_UsesOnlyNativeAppLogoRenderer()
    {
        // Der Bankdialog gehoert seit #125 zu den Bankverbindungen, nicht zur Kontenseite - die Regel
        // ist dieselbe geblieben, nur die Datei ist eine andere.
        var connections = ReadAsset("pages", "settings", "bank-connections", "page.js");
        var ux = ReadAsset("pages", "accounts", "presentation.js");

        Assert.Contains("logo.className='bank-option-logo'", connections);
        Assert.DoesNotContain("decorateBankPicker", ux);
        Assert.DoesNotContain("className='bank-logo'", ux);
    }

    [Fact]
    public async Task AccountsStyles_HoverDoesNotMoveLargeInteractiveSurfaces_AndMobileEditorExists()
    {
        var css = await GetAsync("/pages/accounts/page.css");

        Assert.Contains("transform: none !important", css);
        Assert.Contains(".panel:hover", css);
        Assert.Contains(".table-panel:hover", css);
        Assert.Contains(".account-group-savebar", css);
        Assert.Contains(".account-bank-badge", css);
        Assert.Contains("@media (max-width: 760px)", css);
        Assert.Contains("height: 100dvh", css);
    }

    [Fact]
    public async Task MobileAccountsUseCompactOverflowActions()
    {
        var accounts = await GetAsync("/pages/accounts/page.js");
        var css = await GetAsync("/pages/accounts/page.css");

        Assert.Contains("data-account-more", accounts);
        Assert.Contains("openAccountActionsDialog", accounts);
        Assert.Contains("editAccountVisualById", accounts);
        Assert.Contains(".account-more", css);
        // Seit #125 ist das Auslassungszeichen die EINZIGE Aktion der Zeile - die Kette aus Stift,
        // Ordner, Kontostand, Papierkorb und Coach ist weg, nicht nur auf dem Handy versteckt.
        Assert.DoesNotContain("[data-rename-account]", accounts);
        Assert.DoesNotContain(".account-coach-button", css);
        Assert.Contains("accounts.details", accounts);
    }

    [Fact]
    public async Task BankLogoCsp_ExtendsImagesOnly()
    {
        using var response = await _client.GetAsync("/");
        response.EnsureSuccessStatusCode();

        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values));
        var csp = string.Join(" ", values!);

        Assert.Contains("img-src 'self' data: https://enablebanking.com https://*.enablebanking.com", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.DoesNotContain("script-src 'self' https://", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connect-src 'self' https://", csp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ing_DefaultsToOwnedFinTs_WithoutRequiringEnableBanking()
    {
        var connections = ReadAsset("pages", "settings", "bank-connections", "page.js");
        var de = ReadAsset("locales", "de.json");

        Assert.Contains("fullworthProvider:'fints'", connections);
        Assert.Contains("api/banking/fints/ing/connect", connections);
        Assert.Contains("api/banking/fints/connections/", connections);
        Assert.Contains("ingFinTsFull", de);
        Assert.Contains("ingEnableBankingOnly", de);
    }

    /// <summary>
    /// The server has accepted a manual balance for a connection-less account since P0-4, and that
    /// deliberately includes a finanzguru-import account: the export file carries only bookings, so the
    /// import creates the account archived and out of net worth until someone gives it a balance.
    ///
    /// The list did neither. It hid every archived account and it only rendered the balance affordance
    /// for provider === 'manual', so an imported account was invisible AND unreachable - the money sat
    /// outside net worth with no path in the app to fix it. Both gates are asserted here.
    /// </summary>
    [Fact]
    public async Task AccountsList_OffersAManualBalanceToAnImportedAccount()
    {
        var js = await GetAsync("/pages/accounts/page.js");

        Assert.Contains("const canSetBalance", js);
        Assert.Contains("canSetBalance(x)", js);
        Assert.Contains("canSetBalance(account)", js);
        // The row action must not be back on the manual-provider gate.
        Assert.DoesNotContain("const balanceBtn=isManual", js);
        Assert.Contains("accounts.needsBalance", js);

        // Die Schranke ist die VERBINDUNG, nicht das Provider-Etikett. Hier stand zweimal
        // 'finanzguru-import': einmal als zusaetzliche Erlaubnis fuer den Kontostand, einmal als
        // Ausnahme, damit das archivierte Importkonto ueberhaupt in der Liste auftaucht. Beides war
        // noetig, solange ein Import ein stillgelegtes Konto anlegte - seit er ein richtiges anlegt,
        // waere jede dieser Ausnahmen eine Sonderregel fuer einen Fall, den es nicht mehr gibt.
        Assert.DoesNotContain("'finanzguru-import'", js);
    }

    /// <summary>
    /// Enable Banking's /aspsps only returns the banks the API application is ENABLED for. With a private
    /// application a bank that exists at Enable Banking is simply absent from the picker until it has been
    /// added there - and the picker said "Keine Einträge", which reads as "not supported", the one thing
    /// it does not mean. The empty state has to explain it and link to the application.
    /// </summary>
    [Fact]
    public async Task BankPicker_ExplainsThatOnlyEnabledInstitutionsAppear()
    {
        var js = await GetAsync("/pages/settings/bank-connections/page.js");

        Assert.Contains("bankingSetup.bankMissingHint", js);
        Assert.Contains("https://enablebanking.com/cp/applications", js);
        Assert.DoesNotContain("if(!banks.length)box.innerHTML", js);
    }

    /// <summary>
    /// Drei Beschwerden, ein Ablauf. "Verbinden" tat nichts sichtbar (der Knopf hatte gar keinen
    /// Handler, und danach lief die Anmeldung wortlos), es gab keine Frage, welche Konten gewuenscht
    /// sind, und die Konten standen anschliessend einfach da.
    ///
    /// Geprueft wird deshalb der Faden, nicht die Optik: der Knopf ist verdrahtet, die Anmeldung sagt
    /// waehrenddessen etwas, und sie endet in der Auswahl - nicht in einem Neuladen.
    /// </summary>
    [Fact]
    public async Task ConnectingABank_SaysSomething_AndAsksWhichAccountsAreWanted()
    {
        var js = await GetAsync("/pages/settings/bank-connections/page.js");

        // Der Knopf war tot: die Seite hat ihn gesucht und nie etwas an ihn gehaengt.
        Assert.Contains("addButton.onclick", js);
        // Waehrend der Anmeldung steht etwas da, und die Zeile ist fuer Vorleseprogramme angemeldet.
        Assert.Contains("data-connect-status", js);
        Assert.Contains("aria-live=\"polite\"", js);
        Assert.Contains("bankingSetup.ingConnecting", js);
        // Der Abschluss ist die Auswahl - weder ein stilles Neuladen noch ein blosser Hinweis.
        Assert.Contains("else openIngSelection(result);", js);
        Assert.Contains("function openIngSelection", js);
        Assert.DoesNotContain("toast(get('bankingSetup.ingConnected'));await ctx.reload()", js);
        // Uebernommen wird erst auf Zuruf, mit den abgewaehlten Schluesseln im Rumpf.
        Assert.Contains("/import'", js);
        Assert.Contains("jsonBody({hidden:[...hidden]})", js);
        // Eine abgebrochene Auswahl bleibt erreichbar, ohne die Bank erneut zu fragen.
        Assert.Contains("data-finish-selection", js);
        Assert.Contains("health==='selection_pending'", js);
    }

    /// <summary>
    /// Die Zahlen im Abschluss brauchen ihr eigenes Wort: "1 Depots" stand dort, bis Einzahl und
    /// Mehrzahl getrennte Schluessel bekamen - so, wie die Kontenliste es seit jeher macht.
    /// </summary>
    [Fact]
    public void TheImportSummaryCountsInWholeWords()
    {
        var js = ReadAsset("pages", "settings", "bank-connections", "page.js");
        var de = ReadAsset("locales", "de.json");
        var en = ReadAsset("locales", "en.json");

        Assert.Contains("countLabel=(one,many,n)", js);
        Assert.Contains("accounts.countOne", js);
        Assert.Contains("bankingSetup.ingDepotOne", js);
        foreach (var locale in new[] { de, en })
        {
            Assert.Contains("\"ingDepotOne\"", locale);
            Assert.Contains("\"ingDepotMany\"", locale);
            Assert.Contains("\"ingImportedNone\"", locale);
        }
    }

    /// <summary>
    /// Das Depot steht als eigene Zeile in der Kontenliste, und die Zeile erklaert sich.
    ///
    /// Sonst fragt man sich, warum die Summe ueber der Liste den Betrag enthaelt und das
    /// Nettovermoegen ihn als Depot fuehrt - dasselbe Geld, einmal gezaehlt, an zwei Stellen benannt.
    /// Beide Summen bleiben wie sie sind; erklaert wird der Unterschied, nicht wegdefiniert.
    /// </summary>
    [Fact]
    public async Task ADepotRowSaysWhereItsValueComesFrom()
    {
        var js = await GetAsync("/pages/accounts/page.js");
        var de = ReadAsset("locales", "de.json");
        var en = ReadAsset("locales", "en.json");

        Assert.Contains("x.accountType==='securities'", js);
        Assert.Contains("accounts.depotValue", js);
        Assert.Contains("accounts.depotValueHint", js);
        // Unter dem Betrag, nicht neben dem Namen - dort steht die Frage.
        Assert.Contains("${meaningLine}${depotLine}", js);
        foreach (var locale in new[] { de, en })
        {
            Assert.Contains("\"depotValue\"", locale);
            Assert.Contains("\"depotValueHint\"", locale);
        }
    }

    /// <summary>
    /// "Ich habe alle vier gesehen - und danach stand da '4 Konten importiert'. Wo ist mein Depot?"
    ///
    /// Der Abruf war abgebrochen, vier Konten standen schon, das Depot nicht - und der Dialog meldete
    /// eine glatte Zahl unter der Ueberschrift "Fertig". Er muss das Gegenteil tun: benennen, was
    /// nicht entstanden ist, den Grund zeigen und einen zweiten Versuch anbieten.
    /// </summary>
    [Fact]
    public async Task AnAbortedImportIsNeverPresentedAsFinished()
    {
        var js = await GetAsync("/pages/settings/bank-connections/page.js");
        var de = ReadAsset("locales", "de.json");
        var en = ReadAsset("locales", "en.json");

        // Was fehlt, erkennt der Dialog an der fehlenden Konto-Id - und nennt es beim Namen.
        Assert.Contains("filter(a=>!a.accountId)", js);
        Assert.Contains("bankingSetup.ingImportMissing", js);
        Assert.Contains("bankingSetup.ingImportError", js);
        // Andere Ueberschrift, anderer Knopf: "Fertig" und "Schliessen" waeren beide unwahr.
        Assert.Contains("bankingSetup.ingDonePartly", js);
        Assert.Contains("data-retry", js);
        // Und die Ueberschrift darf nicht mehr unbedingt "Fertig" sein.
        Assert.DoesNotContain("title.textContent=get('bankingSetup.ingDone');", js);
        foreach (var locale in new[] { de, en })
        {
            Assert.Contains("\"ingImportMissing\"", locale);
            Assert.Contains("\"ingDonePartly\"", locale);
            Assert.Contains("\"retrySync\"", locale);
        }
    }

    /// <summary>
    /// Ein Abgleich, der schiefging, ist keine kaputte Anmeldung - und darf deshalb nicht "Neu
    /// verbinden" als einzige Handlung anbieten. Das wirft eine gute Sitzung weg und verlangt die
    /// PIN erneut, fuer einen Fehler, den ein zweiter Versuch behebt.
    ///
    /// Dreimal dieselbe Falle, dreimal derselbe Ausgang: tan_required, selection_pending und jetzt
    /// error. Neu verbinden bleibt den Zustaenden, in denen die Freigabe wirklich weg ist.
    /// </summary>
    [Fact]
    public async Task AFailedSyncOffersARetryAndNotAFullReconnect()
    {
        var js = await GetAsync("/pages/settings/bank-connections/page.js");

        Assert.Contains("health==='error'||health==='partial_history'", js);
        Assert.Contains("data-retry-sync", js);
        Assert.Contains("accounts.retrySync", js);
        // Der zweite Versuch ist ein erzwungener Abgleich - die Sperrfrist darf ihn nicht schlucken.
        Assert.Contains("sync?force=true", js);
        // Und "Neu verbinden" bleibt fuer die Zustaende, in denen die Freigabe wirklich weg ist.
        Assert.Contains("data-reconnect", js);
    }

    /// <summary>
    /// Ein Fehler muss weitergebbar sein. "Fehler beim Abgleich" allein ist nichts, was in ein Issue
    /// passt - der Code steht jetzt in der Zeile, und der Verlauf gibt den ganzen Bericht heraus.
    ///
    /// Und er beschreibt die VERBINDUNG, nicht ihren Inhaber: ein Bericht ueber eine Bankverbindung
    /// landet in einem oeffentlichen Repository. Deshalb steht hier, was NICHT drin sein darf.
    /// </summary>
    [Fact]
    public async Task AConnectionErrorIsVisibleAndCanBeCopiedForAnIssue()
    {
        var js = await GetAsync("/pages/settings/bank-connections/page.js");

        Assert.Contains("const errorCode=x.lastError?", js);
        Assert.Contains("data-copy-report", js);
        Assert.Contains("function connectionReport", js);
        Assert.Contains("navigator.clipboard.writeText", js);
        // Ohne Zwischenablage-Recht bleibt der Bericht erreichbar statt zu verschwinden.
        Assert.Contains("data-report-text", js);

        // Der Bericht nennt die Verbindung - nie das Konto dahinter.
        var report = js[js.IndexOf("function connectionReport", StringComparison.Ordinal)..];
        report = report[..report.IndexOf("async function copyReport", StringComparison.Ordinal)];
        foreach (var forbidden in new[] { "iban", "Iban", "accountNumber", "displayName", "providerSessionId", "authorizationId" })
            Assert.DoesNotContain(forbidden, report);
    }

    /// <summary>
    /// Ein Klick auf ein Depot fuehrt zur Vermoegensansicht, nicht in die Buchungen.
    ///
    /// "Wenn ich draufklicke will ich meine ETF und den Verlauf sehen" - stattdessen kam eine leere
    /// Liste. Kein Zufall und kein Datenfehler: ein Depot HAT keine Buchungen. Die Bank liefert dafuer
    /// eine Bestandsaufstellung (HKWPD), keine Umsaetze; Kaeufe waeren ein eigener Geschaeftsvorfall.
    /// Die leere Liste war also technisch korrekt und trotzdem die falsche Antwort auf den Klick.
    /// </summary>
    [Fact]
    public async Task ClickingADepotOpensTheWealthViewInsteadOfAnEmptyBookingList()
    {
        var js = await GetAsync("/pages/accounts/page.js");

        Assert.Contains("x.accountType==='securities'", js);
        Assert.Contains("ctx.showView('networth')", js);
        // Und jedes andere Konto geht weiterhin in seine Buchungen.
        Assert.Contains("ctx.showView('transactions',{query:'accountId='", js);
    }

    private async Task<string> GetAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// Eine gesunde Verbindung darf keine Sackgasse sein.
    ///
    /// Gemeldet als "brauche vielleicht einen Knopf, falls man neue Konten syncen will, die man
    /// angelegt hat". Die Kontenliste entsteht beim Verbinden; wer bei seiner Bank ein Konto
    /// dazunimmt, musste sie also neu holen koennen. Der Ablauf dafuer war fertig - bei FinTS IST
    /// "Neu verbinden" der Auswahl-Wizard -, nur wurde er ausschliesslich bei Stoerungen angeboten.
    /// Wessen Verbindung lief, kam nicht hin.
    ///
    /// Deshalb KEIN zweiter Knopf mit demselben Ablauf unter neuem Namen, sondern ein Weg zu dem
    /// einen: hinter dem Auslassungszeichen, und bei FinTS unter dem Namen, der sagt, wozu man ihn
    /// hier braucht.
    /// </summary>
    [Fact]
    public async Task AHealthyConnectionStillOffersAWayToReadItsAccountsAgain()
    {
        var js = await GetAsync("/pages/settings/bank-connections/page.js");

        Assert.Contains("data-connection-more", js);
        Assert.Contains("function openConnectionActionsDialog", js);
        // Der Eintrag traegt bei FinTS den Namen der Absicht, fuehrt aber auf denselben Ablauf.
        Assert.Contains("accounts.rediscoverAccounts", js);
        Assert.Contains("if(action==='reconnect')reconnectConnection(connection)", js);
        // Ein Blatt, das niemand oeffnet, ist keine Handlung.
        Assert.Contains("dlg.showModal()", js);
    }
}
