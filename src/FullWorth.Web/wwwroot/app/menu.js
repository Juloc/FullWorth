// Die Menüdefinition. Einzige Quelle.
//
// Aus dieser Datei entstehen alle drei Darstellungen: die Seitenleiste am Desktop, die vier
// Schnellziele der unteren Leiste und der komplette Baum hinter „Mehr". Sie unterscheiden sich in
// der Form, nie im Inhalt — ein Test vergleicht sie gegen genau diese Liste.
//
// Die Seitenleiste und die untere Leiste stehen als fertiges Markup in index.html, damit beim Laden
// nichts eingefügt wird und nichts springt. `ops/generate-menu.mjs` schreibt dieses Markup aus der
// Definition hier; MenuParityTests hält beides zusammen.

// icon ist die ID eines Symbols in wwwroot/icons/sprite.svg - dieselbe, die NavigationCatalog nennt
// (#154). Die Geometrie steht nur dort; NavigationCatalogParityTests haelt die IDs zusammen.

// view: der Name der Ansicht. admin: der Eintrag bleibt verborgen, solange die Sitzung keine
// Adminrechte hat. Ein href gibt es nicht mehr — jeder Eintrag führt in dieselbe Hülle.
// admin: true blendet den Eintrag aus, solange die Sitzung keine Adminrechte hat.
export const MENU = [
  { id: 'overview', label: 'nav.group.overview', items: [
    { view: 'dashboard', label: 'nav.start', icon: 'nav-dashboard' },
    { view: 'insights', label: 'nav.insights', icon: 'nav-insights' },
    { view: 'coach', label: 'nav.coach', icon: 'nav-coach' }
  ] },
  { id: 'finances', label: 'nav.group.finances', items: [
    { view: 'accounts', label: 'nav.accounts', icon: 'nav-accounts' },
    { view: 'transactions', label: 'nav.transactions', icon: 'nav-transactions' },
    { view: 'purchases', label: 'nav.purchases', icon: 'nav-purchases' },
    { view: 'merchants', label: 'nav.merchants', icon: 'nav-merchants' },
    { view: 'collections', label: 'nav.collections', icon: 'nav-collections' }
  ] },
  { id: 'planning', label: 'nav.group.planning', items: [
    { view: 'budgets', label: 'nav.budgets', icon: 'nav-budgets' },
    { view: 'contracts', label: 'nav.contracts', icon: 'nav-contracts' },
    { view: 'compensation', label: 'nav.compensation', icon: 'nav-compensation' },
    { view: 'pension', label: 'nav.pension', icon: 'nav-pension' },
    { view: 'tax', label: 'nav.tax', icon: 'nav-tax' }
  ] },
  { id: 'analysis', label: 'nav.group.analysis', items: [
    { view: 'analytics', label: 'nav.analytics', icon: 'nav-analytics' },
    { view: 'networth', label: 'nav.networth', icon: 'nav-networth' }
  ] },
  { id: 'administration', label: 'nav.group.administration', items: [
    { view: 'categories', label: 'nav.categories', icon: 'nav-categories' },
    { view: 'rules', label: 'nav.rules', icon: 'nav-rules' },
    { view: 'notifications', label: 'nav.notifications', icon: 'nav-notifications' },
    { view: 'audit', label: 'nav.audit', icon: 'nav-audit' },
    { view: 'settings', label: 'nav.settings', icon: 'nav-settings' },
    { view: 'admin', label: 'nav.admin', icon: 'nav-admin', admin: true }
  ] }
];

// Die vier Schnellziele der unteren Leiste. Sie sind eine Abkürzung, keine Auswahl: alles andere
// steht hinter „Mehr", und dort steht auch diese Vier.
export const QUICK = ['dashboard', 'accounts', 'transactions', 'analytics'];

export const ENTRIES = MENU.flatMap(group => group.items);
export const VIEWS = ENTRIES.filter(entry => !entry.href).map(entry => entry.view);
