namespace FullWorth.Web.Navigation;

/// <summary>Eine Gruppe der Navigation. <paramref name="Label"/> ist ein i18n-Schluessel, kein Text.</summary>
public sealed record NavigationGroup(string Id, string Label, IReadOnlyList<NavigationEntry> Items);

/// <summary>
/// Ein Eintrag. <paramref name="View"/> ist der Name, den die alte Huelle benutzt, und zugleich der
/// Schluessel, ueber den Razor-Seite und Eintrag zueinander finden. <paramref name="Icon"/> ist nur
/// der INHALT des &lt;svg&gt; - Rahmen, Groesse und Strichstaerke kommen aus shell.css.
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
            new("dashboard", "/", "nav.start", "<rect x=\"3\" y=\"3\" width=\"8\" height=\"8\" rx=\"2\"/><rect x=\"13\" y=\"3\" width=\"8\" height=\"8\" rx=\"2\"/><rect x=\"3\" y=\"13\" width=\"8\" height=\"8\" rx=\"2\"/><rect x=\"13\" y=\"13\" width=\"8\" height=\"8\" rx=\"2\"/>"),
            new("insights", "/insights", "nav.insights", "<path d=\"M12 3a6 6 0 0 0-3.5 10.9V17h7v-3.1A6 6 0 0 0 12 3Z\"/><path d=\"M10 20h4\"/>"),
            new("coach", "/coach", "nav.coach", "<path d=\"M5 5.5h14v10H9l-4 3v-13Z\"/><path d=\"M9 9h6m-6 3h4\"/>"),
        ]),
        new("finances", "nav.group.finances",
        [
            new("accounts", "/accounts", "nav.accounts", "<path d=\"M3 9.5 12 4l9 5.5\"/><path d=\"M5 10v7m4.5-7v7m5-7v7m4.5-7v7\"/><path d=\"M3 20h18\"/>"),
            new("transactions", "/transactions", "nav.transactions", "<path d=\"M7 4v13m0 0-3-3m3 3 3-3\"/><path d=\"M17 20V7m0 0-3 3m3-3 3 3\"/>"),
            new("purchases", "/purchases", "nav.purchases", "<path d=\"M6 3h12v18l-2-1.5L14 21l-2-1.5L10 21l-2-1.5L6 21Z\"/><path d=\"M9 8h6m-6 4h6\"/>"),
            new("merchants", "/merchants", "nav.merchants", "<path d=\"M4 9h16l-1 3H5Z\"/><path d=\"M5 12v8h14v-8\"/><path d=\"M4 9 6 4h12l2 5\"/>"),
            new("collections", "/collections", "nav.collections", "<path d=\"M4 7h6l2 2h8v9a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2Z\"/><path d=\"M4 7V5.5A1.5 1.5 0 0 1 5.5 4h4\"/>"),
        ]),
        new("planning", "nav.group.planning",
        [
            new("budgets", "/budgets", "nav.budgets", "<circle cx=\"12\" cy=\"12\" r=\"8.5\"/><path d=\"M12 3.5V12l6 4\"/>"),
            new("contracts", "/contracts", "nav.contracts", "<path d=\"M7 3h7l4 4v14H7Z\"/><path d=\"M14 3v4h4\"/><path d=\"M10 12h5m-5 4h5\"/>"),
            new("compensation", "/compensation", "nav.compensation", "<path d=\"M4 18V8m5 10V5m5 13v-7m5 7V9\"/><path d=\"M3 21h18\"/>"),
            new("pension", "/pension", "nav.pension", "<path d=\"M12 3 4 7v5c0 4.4 3.4 8 8 9 4.6-1 8-4.6 8-9V7Z\"/><path d=\"M9 12.5 11 15l4-4.5\"/>"),
            new("tax", "/tax", "nav.tax", "<path d=\"M5 4h14v16H5Z\"/><path d=\"M8 8h8M8 12h2m4 0h2M8 16h2m4 0h2\"/>"),
        ]),
        new("analysis", "nav.group.analysis",
        [
            new("analytics", "/analytics", "nav.analytics", "<path d=\"M4 20V10m5.5 10V4m5.5 16v-8m5 8V7\"/>"),
            new("networth", "/networth", "nav.networth", "<path d=\"m2 17 6-6 4 4 10-10\"/><path d=\"M16 5h6v6\"/>"),
        ]),
        new("administration", "nav.group.administration",
        [
            new("categories", "/categories", "nav.categories", "<path d=\"M4 6h6l2 2h8v10H4Z\"/>"),
            new("rules", "/rules", "nav.rules", "<path d=\"M4 7h16M4 12h16M4 17h16\"/><circle cx=\"9\" cy=\"7\" r=\"2\"/><circle cx=\"15\" cy=\"12\" r=\"2\"/><circle cx=\"7\" cy=\"17\" r=\"2\"/>"),
            new("notifications", "/notifications", "nav.notifications", "<path d=\"M6 9a6 6 0 0 1 12 0c0 5 2 6 2 6H4s2-1 2-6\"/><path d=\"M10 20a2 2 0 0 0 4 0\"/>"),
            new("audit", "/audit", "nav.audit", "<path d=\"M5 4h14v16H5Z\"/><path d=\"M8 9h8M8 13h8M8 17h4\"/>"),
            new("settings", "/settings", "nav.settings", "<circle cx=\"12\" cy=\"12\" r=\"3.2\"/><path d=\"M12 3v2m0 14v2m9-9h-2M5 12H3m14.5-6.5-1.4 1.4M7.9 16.1l-1.4 1.4m0-11.9 1.4 1.4m8.2 8.2 1.4 1.4\"/>"),
            new("admin", "/admin", "nav.admin", "<path d=\"M12 3 19 6v5c0 4.5-2.8 8-7 10-4.2-2-7-5.5-7-10V6Z\"/><path d=\"M9.5 12h5M12 9.5v5\"/>", AdminOnly: true),
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
