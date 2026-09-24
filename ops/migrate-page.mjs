// Eine Seite aus der alten Hülle in eine Razor-Seite überführen (#154 Phase C).
//
// Der Umzug ist bei jeder Seite derselbe, und genau deshalb steht er als Skript da und nicht als
// Anleitung: was sich wiederholt, soll sich nicht bei der fünfzehnten Seite anders wiederholen als
// bei der ersten.
//
//   node ops/migrate-page.mjs <view>
//
// Was es tut:
//   1. wwwroot/pages/<pfad>/page.html  ->  Pages/<Name>/Index.cshtml   (und löscht die page.html,
//      womit generate-shell.mjs die Seite von selbst aus der Hülle nimmt)
//   2. legt wwwroot/pages/<pfad>/entry.js an
//
// Was es NICHT tut, weil es jedes Mal anders aussieht: den Import und die Registrierung aus app.js
// entfernen und den Eintrag in MIGRATED aufnehmen. Das bleibt Handarbeit mit Augenmaß.

import { readFileSync, writeFileSync, rmSync, mkdirSync, existsSync } from 'node:fs';

const NL = String.fromCharCode(10);
const CRNL = String.fromCharCode(13, 10);
const root = new URL('../src/FullWorth.Web/', import.meta.url);
const wwwroot = new URL('wwwroot/', root);

/**
 * view      der Name, unter dem die alte Hülle die Ansicht kennt
 * folder    der Ordner unter pages/ (meist gleich dem view)
 * page      der Ordner unter Pages/
 * route     die öffentliche Adresse
 * render    der Export, der zeichnet
 * bind      der Export, der einmalig bindet (optional)
 * action    [i18n-Schlüssel, Export] für die Hauptaktion in der Topbar (optional)
 */
export const PAGES = {
  audit: { folder: 'audit', page: 'Audit', route: '/audit', render: 'renderAudit', bind: 'bindAudit' },
  notifications: { folder: 'notifications', page: 'Notifications', route: '/notifications', render: 'renderNotifications' },
  merchants: {
    folder: 'merchants', page: 'Merchants', route: '/merchants',
    render: 'renderMerchants', bind: 'bindMerchants', action: ['merchants.new', 'newMerchant']
  },
  rules: {
    folder: 'rules', page: 'Rules', route: '/rules',
    render: 'renderRules', bind: 'bindRules', action: ['rules.new', 'newRule']
  },
  categories: {
    folder: 'categories', page: 'Categories', route: '/categories',
    render: 'renderCategories', bind: 'bindCategories', action: ['categories.new', 'newCategory']
  },
  collections: {
    folder: 'collections', page: 'Collections', route: '/collections',
    render: 'renderCollections', bind: 'bindCollections', action: ['collections.new', 'newCollection']
  },
  analytics: { folder: 'analytics', page: 'Analytics', route: '/analytics', render: 'renderAnalytics', bind: 'bindAnalytics' },
  tax: { folder: 'tax', page: 'Tax', route: '/tax', render: 'renderTax', bind: 'bindTax' },
  pension: { folder: 'pension', page: 'Pension', route: '/pension', render: 'renderPension', bind: 'bindPension' },
  admin: { folder: 'admin', page: 'Admin', route: '/admin', render: 'renderAdmin' },
  compensation: { folder: 'compensation', page: 'Compensation', route: '/compensation', render: 'renderCompensation' },
  insights: { folder: 'insights', page: 'Insights', route: '/insights', render: 'mountInsights' },
  coach: { folder: 'coach', page: 'Coach', route: '/coach', render: 'renderCoach' },
  purchases: {
    folder: 'purchases', page: 'Purchases', route: '/purchases',
    render: 'renderPurchases', bind: 'bindPurchases'
  },
  contracts: {
    folder: 'contracts', page: 'Contracts', route: '/contracts',
    render: 'renderContracts', bind: 'bindContracts', action: ['contracts.new', 'newContract']
  },
  budgets: {
    folder: 'budgets', page: 'Budgets', route: '/budgets',
    render: 'renderBudgets', action: ['budgets.new', 'newBudget']
  },
  // Unterseiten: sie stehen nicht in der Seitenleiste, ihr Elterneintrag schon. Welcher, sagt
  // NavigationCatalog.SubPages - _Navigation.cshtml markiert ihn von dort aus.
  passkeys: {
    folder: 'settings/security/passkeys', page: 'Settings/Security/Passkeys',
    route: '/settings/security/passkeys', render: 'renderPasskeys'
  },
  intelligence: {
    folder: 'settings/intelligence', page: 'Settings/Intelligence',
    route: '/settings/intelligence', render: 'renderIntelligence'
  },
  import: {
    folder: 'settings/import', page: 'Settings/Import',
    route: '/settings/import', render: 'renderImportCenter'
  },
  'import-finanzguru-xlsx': {
    folder: 'settings/import/finanzguru/xlsx', page: 'Settings/Import/Finanzguru/Xlsx',
    route: '/settings/import/finanzguru/xlsx', render: null
  },
  'bank-connections': {
    folder: 'settings/bank-connections', page: 'Settings/BankConnections',
    route: '/settings/bank-connections', render: 'renderBankConnections'
  },
  accounts: {
    folder: 'accounts', page: 'Accounts', route: '/accounts',
    render: 'renderAccounts', bind: 'bindAccounts', action: ['accounts.add', 'openAddAccount']
  },
  'account-detail': {
    folder: 'accounts/detail', page: 'Accounts/Detail', route: '/accounts/detail',
    render: 'renderAccountDetail'
  },
  networth: {
    folder: 'networth', page: 'NetWorth', route: '/networth',
    render: 'renderNetWorth', bind: 'bindNetWorth', action: ['networth.newAsset', 'newAsset']
  },
  settings: {
    folder: 'settings', page: 'Settings', route: '/settings',
    render: 'renderSettings', bind: 'bindSettings'
  },
  'import-broker-pdf': {
    folder: 'settings/import/broker-pdf', page: 'Settings/Import/BrokerPdf',
    route: '/settings/import/broker-pdf', render: 'renderBrokerPdfImport'
  }
};

/** Dieselbe Regel wie serverseitig in PageHeadings: Seitentitel, sonst der Name aus der Navigation. */
function title(view) {
  const locales = JSON.parse(readFileSync(new URL('locales/de.json', wwwroot), 'utf8'));
  const heading = locales.pages?.[view]?.title ?? locales.nav?.[view];
  if (!heading) throw new Error(`locales/de.json kennt weder pages.${view}.title noch nav.${view}.`);
  return heading;
}

export function migrate(view) {
  const spec = PAGES[view];
  if (!spec) throw new Error(`Kein Bauplan für ${view}.`);
  // Eine Seite ohne Zeichenfunktion gibt es: das Modul baut sich beim Laden selbst auf. Der
  // erzeugte Einstieg passt dann aber nicht, und aus 'render: null' wurde einmal wortwoertlich
  // 'await null(context)'. Solche Seiten bekommen ihren Einstieg von Hand.
  if (!spec.render) throw new Error(`${view} hat keine Zeichenfunktion - entry.js bitte von Hand schreiben.`);

  const htmlPath = new URL(`pages/${spec.folder}/page.html`, wwwroot);
  if (!existsSync(htmlPath)) throw new Error(`pages/${spec.folder}/page.html gibt es nicht (schon umgezogen?).`);
  const html = readFileSync(htmlPath, 'utf8').trim();
  if (html.includes('@')) throw new Error('Das Markup enthält @ - Razor müsste escapen, bitte von Hand.');

  // Die Sichtbarkeitsklasse der alten Hülle fällt weg: auf einer echten Seite gibt es nichts
  // umzuschalten, und .view:not(.active){display:none} würde die Seite sonst verstecken.
  //
  // Nur "view" verschwindet, nicht das ganze Attribut: mehrere Seiten tragen daneben eine eigene
  // Klasse (class="view tax-view"), an der ihr page.css hängt. Wer das Attribut im Ganzen
  // entfernt, nimmt der Seite ihr halbes Aussehen - und zwar lautlos.
  let found = false;
  const body = html.replace(
    /(<section id="view-[a-z-]+")\s+class="([^"]*)"/,
    (all, open, classes) => {
      const rest = classes.split(/\s+/).filter(name => name && name !== 'view');
      if (rest.length === classes.split(/\s+/).filter(Boolean).length) return all;
      found = true;
      return rest.length ? `${open} class="${rest.join(' ')}"` : open;
    });
  if (!found) throw new Error('Der <section class="view">-Rahmen sieht anders aus als erwartet.');

  const cshtml = [
    `@page "${spec.route}"`,
    '@{',
    `    ViewData["Title"] = "${title(view)} · FullWorth";`,
    `    ViewData["ActiveView"] = "${view}";`,
    '}',
    '',
    '@*',
    '    Eine eigene Seite (#154 Phase C): ihr Markup, ihr CSS, ihr JS - und nichts von den anderen.',
    '    Das Markup stand bis hierher in wwwroot/pages/' + spec.folder + '/page.html, von wo',
    '    ops/generate-shell.mjs es in das eine grosse Dokument geschrieben hat.',
    '*@',
    '',
    body,
    '',
    '@section Styles {',
    `    <link rel="stylesheet" href="/pages/${spec.folder}/page.css">`,
    '}',
    '',
    '@section Scripts {',
    `    <script type="module" src="/pages/${spec.folder}/entry.js"></script>`,
    '}',
    ''
  ].join(CRNL);

  mkdirSync(new URL(`Pages/${spec.page}/`, root), { recursive: true });
  writeFileSync(new URL(`Pages/${spec.page}/Index.cshtml`, root), cshtml);
  // Danach noch: ops/localise-razor.mjs uebersetzt die data-i18n-Beschriftungen serverseitig.
  rmSync(htmlPath);

  const imports = [spec.render, spec.bind, spec.action?.[1]].filter(Boolean).join(', ');
  const depth = '../'.repeat(spec.folder.split('/').length + 1);
  const entry = [
    `// Der Einstieg der Seite "${view}" (#154).`,
    '//',
    '// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite',
    '// gleich. page.js selbst hat sich nicht geaendert: es bekommt denselben Kontext wie vorher aus der',
    '// alten Huelle, nur dass ihn jetzt die Seite erzeugt statt app.js fuer alle.',
    '',
    `import { startShellPage } from '${depth}app/shell.js';`,
    `import { ${imports} } from './page.js';`,
    '',
    ...(spec.action
      ? ['// Die Hauptaktion der Topbar gehoert der Seite. Der Kontext steht erst fest, wenn',
         '// gezeichnet wird - der Knopf wird aber vorher beschriftet, also merkt ihn sich der',
         '// Einstieg und der Klick liest ihn spaeter.',
         'let pageContext;', '']
      : []),
    ...(spec.bind ? ['let bound = false;', ''] : []),
    'await startShellPage(async context => {',
    ...(spec.action ? ['  pageContext = context;'] : []),
    ...(spec.bind
      ? ['  if (!bound) {', `    ${spec.bind}(context);`, '    bound = true;', '  }']
      : []),
    `  await ${spec.render}(context);`,
    '}' + (spec.action
      ? `, { primaryAction: () => ['${spec.action[0]}', () => ${spec.action[1]}(pageContext)] });`
      : ');'),
    ''
  ].join(NL);

  writeFileSync(new URL(`pages/${spec.folder}/entry.js`, wwwroot), entry);
  return spec;
}

const view = process.argv[2];
if (view) {
  migrate(view);
  console.log(`${view} umgezogen.`);
}
