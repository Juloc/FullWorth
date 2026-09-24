// Der Einstieg der Seite "networth" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert: es bekommt denselben Kontext wie vorher aus der
// alten Huelle, nur dass ihn jetzt die Seite erzeugt statt app.js fuer alle.
//
// Zwei Module, eine Seite: das Vermoegen und die Kredite stehen untereinander auf demselben Schirm
// und werden nacheinander gezeichnet - genau in dieser Reihenfolge, wie es die Huelle auch tat.

import { startShellPage } from '../../app/shell.js';
import { renderNetWorth, bindNetWorth, newAsset } from './page.js';
import { renderLoans, bindLoans } from './loans.js';

// Die Hauptaktion der Topbar gehoert der Seite. Der Kontext steht erst fest, wenn
// gezeichnet wird - der Knopf wird aber vorher beschriftet, also merkt ihn sich der
// Einstieg und der Klick liest ihn spaeter.
let pageContext;

let bound = false;

await startShellPage(async context => {
  pageContext = context;
  if (!bound) {
    bindNetWorth(context);
    bindLoans(context);
    bound = true;
  }
  await renderNetWorth(context);
  await renderLoans(context);
}, { primaryAction: () => ['networth.newAsset', () => newAsset(pageContext)] });
