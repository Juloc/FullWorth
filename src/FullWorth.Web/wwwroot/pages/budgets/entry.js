// Der Einstieg der Seite "budgets" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert: es bekommt denselben Kontext wie vorher aus der
// alten Huelle, nur dass ihn jetzt die Seite erzeugt statt app.js fuer alle.

import { startShellPage } from '../../app/shell.js';
import { renderBudgets, newBudget, openBudgetDetail } from './page.js';

// Die Hauptaktion der Topbar gehoert der Seite. Der Kontext steht erst fest, wenn
// gezeichnet wird - der Knopf wird aber vorher beschriftet, also merkt ihn sich der
// Einstieg und der Klick liest ihn spaeter.
let pageContext;

let bound = false;

await startShellPage(async context => {
  pageContext = context;
  if (!bound) {
    // Der "Hinzufuegen"-Knopf der Kopfzeile. Er haengte bis #154 in app.js, weil das Markup dort
    // lag; jetzt gehoert beides der Seite. page.js hat kein bind*, also bindet der Einstieg.
    context.$('[data-action="new-budget"]')?.addEventListener('click', () => newBudget(pageContext));
    bound = true;
  }
  await renderBudgets(context);

  // "Budget oeffnen" aus dem Coach oder aus den Hinweisen. Die Kennung kommt als ?open= an, weil
  // sie einen Seitenwechsel ueberstehen muss - frueher wurde dafuer nach dem Wechsel ein Ereignis
  // geschickt, was nur ging, solange beide Seiten dasselbe Dokument waren. Danach faellt sie aus
  // der Adresse, sonst oeffnete ein Neuladen dieselbe Ansicht wieder.
  const open = new URLSearchParams(location.search).get('open');
  if (open) {
    history.replaceState(null, '', location.pathname);
    await openBudgetDetail(context, open);
  }
}, { primaryAction: () => ['budgets.new', () => newBudget(pageContext)] });
