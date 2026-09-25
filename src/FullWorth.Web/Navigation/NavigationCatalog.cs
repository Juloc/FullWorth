namespace FullWorth.Web.Navigation;

/// <summary>Eine Gruppe der Navigation. <paramref name="Label"/> ist ein i18n-Schluessel, kein Text.</summary>
public sealed record NavigationGroup(string Id, string Label, IReadOnlyList<NavigationEntry> Items);

/// <summary>
/// Ein Eintrag. <paramref name="View"/> ist der Name, den die alte Huelle benutzt, und zugleich der
/// Schluessel, ueber den Razor-Seite und Eintrag zueinander finden. <paramref name="Icon"/> ist die ID
/// eines Symbols in wwwroot/icons/sprite.svg (#154) - die Geometrie steht nur dort. Rahmen, Groesse und
/// Strichstaerke kommen aus shell.css am verweisenden &lt;svg&gt;.
/// </summary>
public sealed record NavigationEntry(string View, string Path, string Label, string Icon, bool AdminOnly = false);

/// <summary>Eine Unterseite. Sie steht in keiner Gruppe, markiert aber ihren Elterneintrag.</summary>
public sealed record NavigationSubPage(string View, string Path, string Parent);

/// <summary>
/// Die Navigation, serverseitig (#154).
///
/// Bis zum Abschluss der Migration steht dieselbe Definition noch in <c>wwwroot/app/menu.js</c>,
/// weil die alte Huelle sie dort liest. Dass die beiden nicht auseinanderlaufen, haelt
/// <c>NavigationCatalogParityTests</c> fest - und wenn die Huelle faellt, faellt die JS-Fassung mit
/// ihr und diese hier bleibt allein uebrig.
///
/// Aus dieser einen Liste entstehen alle Darstellungen: Seitenleiste, die vier Schnellziele der
/// unteren Leiste und der Baum hinter "Mehr". Nie drei gepflegte Menues.
/// </summary>
public static class NavigationCatalog
{
    public static IReadOnlyList<NavigationGroup> Groups { get; } =
    [
        new("overview", "nav.group.overview",
        [
            new("dashboard", "/", "nav.start", "nav-dashboard"),
            new("insights", "/insights", "nav.insights", "nav-insights"),
            new("coach", "/coach", "nav.coach", "nav-coach"),
        ]),
        new("finances", "nav.group.finances",
        [
            new("accounts", "/accounts", "nav.accounts", "nav-accounts"),
            new("transactions", "/transactions", "nav.transactions", "nav-transactions"),
            new("purchases", "/purchases", "nav.purchases", "nav-purchases"),
            new("merchants", "/merchants", "nav.merchants", "nav-merchants"),
            new("collections", "/collections", "nav.collections", "nav-collections"),
        ]),
        new("planning", "nav.group.planning",
        [
            new("budgets", "/budgets", "nav.budgets", "nav-budgets"),
            new("contracts", "/contracts", "nav.contracts", "nav-contracts"),
            new("compensation", "/compensation", "nav.compensation", "nav-compensation"),
            new("pension", "/pension", "nav.pension", "nav-pension"),
            new("tax", "/tax", "nav.tax", "nav-tax"),
        ]),
        new("analysis", "nav.group.analysis",
        [
            new("analytics", "/analytics", "nav.analytics", "nav-analytics"),
            new("networth", "/networth", "nav.networth", "nav-networth"),
        ]),
        new("administration", "nav.group.administration",
        [
            new("categories", "/categories", "nav.categories", "nav-categories"),
            new("rules", "/rules", "nav.rules", "nav-rules"),
            new("notifications", "/notifications", "nav.notifications", "nav-notifications"),
            new("audit", "/audit", "nav.audit", "nav-audit"),
            new("settings", "/settings", "nav.settings", "nav-settings"),
            new("admin", "/admin", "nav.admin", "nav-admin", AdminOnly: true),
        ]),
    ];

    /// <summary>Die vier Schnellziele der unteren Leiste. Eine Abkuerzung, keine Auswahl.</summary>
    public static IReadOnlyList<string> Quick { get; } =
        ["dashboard", "accounts", "transactions", "analytics"];

    public static IReadOnlyList<NavigationSubPage> SubPages { get; } =
    [
        new("passkeys", "/settings/security/passkeys", "settings"),
        new("import", "/settings/import", "settings"),
        new("import-finanzguru-xlsx", "/settings/import/finanzguru/xlsx", "settings"),
        new("import-broker-pdf", "/settings/import/broker-pdf", "settings"),
        new("intelligence", "/settings/intelligence", "settings"),
        new("bank-connections", "/settings/bank-connections", "settings"),
        new("account-detail", "/accounts/detail", "accounts"),
    ];

    public static IEnumerable<NavigationEntry> Entries => Groups.SelectMany(group => group.Items);

    /// <summary>Jede Adresse der angemeldeten Anwendung - Haupteintraege und Unterseiten.</summary>
    public static IEnumerable<string> AllPaths =>
        Entries.Select(entry => entry.Path).Concat(SubPages.Select(page => page.Path));
}
