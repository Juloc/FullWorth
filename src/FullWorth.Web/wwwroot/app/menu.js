// Die Menüdefinition. Einzige Quelle.
//
// Aus dieser Datei entstehen alle drei Darstellungen: die Seitenleiste am Desktop, die vier
// Schnellziele der unteren Leiste und der komplette Baum hinter „Mehr". Sie unterscheiden sich in
// der Form, nie im Inhalt — ein Test vergleicht sie gegen genau diese Liste.
//
// Die Seitenleiste und die untere Leiste stehen als fertiges Markup in index.html, damit beim Laden
// nichts eingefügt wird und nichts springt. `ops/generate-menu.mjs` schreibt dieses Markup aus der
// Definition hier; MenuParityTests hält beides zusammen.

// Nur der Inhalt des <svg>. Rahmen, Größe und Strichstärke kommen aus shell.css — ein Icon, das sein
// eigenes viewBox mitbringt, wäre eine Kopie der Basis.
const icon = {
  start: '<rect x="3" y="3" width="8" height="8" rx="2"/><rect x="13" y="3" width="8" height="8" rx="2"/><rect x="3" y="13" width="8" height="8" rx="2"/><rect x="13" y="13" width="8" height="8" rx="2"/>',
  insights: '<path d="M12 3a6 6 0 0 0-3.5 10.9V17h7v-3.1A6 6 0 0 0 12 3Z"/><path d="M10 20h4"/>',
  coach: '<path d="M5 5.5h14v10H9l-4 3v-13Z"/><path d="M9 9h6m-6 3h4"/>',
  accounts: '<path d="M3 9.5 12 4l9 5.5"/><path d="M5 10v7m4.5-7v7m5-7v7m4.5-7v7"/><path d="M3 20h18"/>',
  transactions: '<path d="M7 4v13m0 0-3-3m3 3 3-3"/><path d="M17 20V7m0 0-3 3m3-3 3 3"/>',
  purchases: '<path d="M6 3h12v18l-2-1.5L14 21l-2-1.5L10 21l-2-1.5L6 21Z"/><path d="M9 8h6m-6 4h6"/>',
  merchants: '<path d="M4 9h16l-1 3H5Z"/><path d="M5 12v8h14v-8"/><path d="M4 9 6 4h12l2 5"/>',
  budgets: '<circle cx="12" cy="12" r="8.5"/><path d="M12 3.5V12l6 4"/>',
  contracts: '<path d="M7 3h7l4 4v14H7Z"/><path d="M14 3v4h4"/><path d="M10 12h5m-5 4h5"/>',
  compensation: '<path d="M4 18V8m5 10V5m5 13v-7m5 7V9"/><path d="M3 21h18"/>',
  pension: '<path d="M12 3 4 7v5c0 4.4 3.4 8 8 9 4.6-1 8-4.6 8-9V7Z"/><path d="M9 12.5 11 15l4-4.5"/>',
  tax: '<path d="M5 4h14v16H5Z"/><path d="M8 8h8M8 12h2m4 0h2M8 16h2m4 0h2"/>',
  analytics: '<path d="M4 20V10m5.5 10V4m5.5 16v-8m5 8V7"/>',
  networth: '<path d="m2 17 6-6 4 4 10-10"/><path d="M16 5h6v6"/>',
  categories: '<path d="M4 6h6l2 2h8v10H4Z"/>',
  rules: '<path d="M4 7h16M4 12h16M4 17h16"/><circle cx="9" cy="7" r="2"/><circle cx="15" cy="12" r="2"/><circle cx="7" cy="17" r="2"/>',
  notifications: '<path d="M6 9a6 6 0 0 1 12 0c0 5 2 6 2 6H4s2-1 2-6"/><path d="M10 20a2 2 0 0 0 4 0"/>',
  audit: '<path d="M5 4h14v16H5Z"/><path d="M8 9h8M8 13h8M8 17h4"/>',
  collections: '<path d="M4 7h6l2 2h8v9a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2Z"/><path d="M4 7V5.5A1.5 1.5 0 0 1 5.5 4h4"/>',
  settings: '<circle cx="12" cy="12" r="3.2"/><path d="M12 3v2m0 14v2m9-9h-2M5 12H3m14.5-6.5-1.4 1.4M7.9 16.1l-1.4 1.4m0-11.9 1.4 1.4m8.2 8.2 1.4 1.4"/>',
  admin: '<path d="M12 3 19 6v5c0 4.5-2.8 8-7 10-4.2-2-7-5.5-7-10V6Z"/><path d="M9.5 12h5M12 9.5v5"/>'
};

// view: der Name der Ansicht. admin: der Eintrag bleibt verborgen, solange die Sitzung keine
// Adminrechte hat. Ein href gibt es nicht mehr — jeder Eintrag führt in dieselbe Hülle.
// admin: true blendet den Eintrag aus, solange die Sitzung keine Adminrechte hat.
export const MENU = [
  { id: 'overview', label: 'nav.group.overview', items: [
    { view: 'dashboard', label: 'nav.start', icon: icon.start },
    { view: 'insights', label: 'nav.insights', icon: icon.insights },
    { view: 'coach', label: 'nav.coach', icon: icon.coach }
  ] },
  { id: 'finances', label: 'nav.group.finances', items: [
    { view: 'accounts', label: 'nav.accounts', icon: icon.accounts },
    { view: 'transactions', label: 'nav.transactions', icon: icon.transactions },
    { view: 'purchases', label: 'nav.purchases', icon: icon.purchases },
    { view: 'merchants', label: 'nav.merchants', icon: icon.merchants },
    { view: 'collections', label: 'nav.collections', icon: icon.collections }
  ] },
  { id: 'planning', label: 'nav.group.planning', items: [
    { view: 'budgets', label: 'nav.budgets', icon: icon.budgets },
    { view: 'contracts', label: 'nav.contracts', icon: icon.contracts },
    { view: 'compensation', label: 'nav.compensation', icon: icon.compensation },
    { view: 'pension', label: 'nav.pension', icon: icon.pension },
    { view: 'tax', label: 'nav.tax', icon: icon.tax }
  ] },
  { id: 'analysis', label: 'nav.group.analysis', items: [
    { view: 'analytics', label: 'nav.analytics', icon: icon.analytics },
    { view: 'networth', label: 'nav.networth', icon: icon.networth }
  ] },
  { id: 'administration', label: 'nav.group.administration', items: [
    { view: 'categories', label: 'nav.categories', icon: icon.categories },
    { view: 'rules', label: 'nav.rules', icon: icon.rules },
    { view: 'notifications', label: 'nav.notifications', icon: icon.notifications },
    { view: 'audit', label: 'nav.audit', icon: icon.audit },
    { view: 'settings', label: 'nav.settings', icon: icon.settings },
    { view: 'admin', label: 'nav.admin', icon: icon.admin, admin: true }
  ] }
];

// Die vier Schnellziele der unteren Leiste. Sie sind eine Abkürzung, keine Auswahl: alles andere
// steht hinter „Mehr", und dort steht auch diese Vier.
export const QUICK = ['dashboard', 'accounts', 'transactions', 'analytics'];

export const ENTRIES = MENU.flatMap(group => group.items);
export const VIEWS = ENTRIES.filter(entry => !entry.href).map(entry => entry.view);
