// Welche Adresse zu welcher Ansicht gehört. Einzige Quelle im Browser.
//
// Die Haupteinträge stehen in app/menu.js, die Unterseiten hier — dieselbe Aufteilung wie serverseitig
// in NavigationCatalog, und NavigationCatalogParityTests hält beide zusammen.
//
// Bis zur Razor-Migration (#154) stand diese Karte in app.js und wurde dort in einen Client-Router
// gefüttert. Sie steht jetzt für sich, weil auch eine Seite ohne Hülle wissen muss, wohin ein
// „öffne die Konten" führt: dort ist es kein Ansichtswechsel mehr, sondern eine echte Adresse.

import { ENTRIES } from './menu.js';

export const SUBPAGES = {
  passkeys: { path: '/settings/security/passkeys', parent: 'settings' },
  import: { path: '/settings/import', parent: 'settings' },
  'import-finanzguru-xlsx': { path: '/settings/import/finanzguru/xlsx', parent: 'settings' },
  'import-broker-pdf': { path: '/settings/import/broker-pdf', parent: 'settings' },
  intelligence: { path: '/settings/intelligence', parent: 'settings' },
  'bank-connections': { path: '/settings/bank-connections', parent: 'settings' },
  // Die Kontodetails liegen unter den Konten, nicht daneben: der Zurückweg führt in die Liste, und
  // die Seitenleiste markiert weiter „Konten".
  'account-detail': { path: '/accounts/detail', parent: 'accounts' }
};

/** Die Startseite ist die Wurzel; alles andere trägt seinen Namen. */
export function pathForView(view) {
  if (!view || view === 'dashboard') return '/';
  return SUBPAGES[view]?.path ?? '/' + view;
}

export const VIEW_PATHS = Object.fromEntries(
  ENTRIES.map(entry => [entry.view, pathForView(entry.view)])
    .concat(Object.entries(SUBPAGES).map(([view, page]) => [view, page.path])));

// Welche Ansichten bereits eine eigene Razor-Seite haben (#154).
//
// Die alte Hülle nimmt sie aus ihrer Liste: dann fängt sie deren Links nicht mehr ab, und ein Klick
// auf „Buchungen" ist wieder das, was er im Markup schon immer war — eine echte Navigation. Die
// Menge wächst mit jeder migrierten Seite und ist leer, wenn die Hülle verschwindet.
export const MIGRATED = new Set([
  'transactions',
  'audit', 'notifications', 'merchants', 'rules', 'categories', 'collections'
]);
